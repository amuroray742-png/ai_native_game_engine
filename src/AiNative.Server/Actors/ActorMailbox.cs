using System;
using System.Collections.Generic;

namespace AiNative.Server.Actors
{
    internal sealed class ActorMailbox<T> where T : notnull
    {
        private readonly object _gate = new object();
        private readonly Queue<(T Message, int Bytes, bool IsControl)> _queue = new Queue<(T, int, bool)>();
        private readonly int _maxMessages;
        private readonly long _maxBytes;
        private readonly Action _wake;
        private readonly int _reservedControlMessages;
        private readonly long _reservedControlBytes;
        private int _regularMessages;
        private long _regularBytes;
        private int _messages;
        private long _bytes;
        private bool _stopped;

        public ActorMailbox(int maxMessages, long maxBytes, Action wake, int reservedControlMessages = 0, long reservedControlBytes = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessages);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            if (reservedControlMessages < 0 || reservedControlMessages >= maxMessages || reservedControlBytes < 0 || reservedControlBytes >= maxBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(reservedControlMessages), "控制预留必须非负且小于总预算。");
            }
            _reservedControlMessages = reservedControlMessages;
            _reservedControlBytes = reservedControlBytes;
            _maxMessages = maxMessages;
            _maxBytes = maxBytes;
            _wake = wake;
        }

        public PostResult TryPost(T message, int byteCount, bool isControl = false)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            lock (_gate)
            {
                if (_stopped)
                {
                    return PostResult.Stopped;
                }
                if (_messages == _maxMessages || byteCount > _maxBytes - _bytes ||
                    (!isControl && (_regularMessages >= _maxMessages - _reservedControlMessages || byteCount > _maxBytes - _reservedControlBytes - _regularBytes)))
                {
                    return PostResult.Full;
                }
                _queue.Enqueue((message, byteCount, isControl));
                if (!isControl) { _regularMessages++; _regularBytes += byteCount; }
                _messages++;
                _bytes += byteCount;
            }
            _wake();
            return PostResult.Accepted;
        }

        public bool TryTake(out (T Message, int Bytes, bool IsControl) entry)
        {
            lock (_gate)
            {
                return _queue.TryDequeue(out entry);
            }
        }

        public void Release(int bytes, bool isControl)
        {
            lock (_gate)
            {
                if (!isControl) { _regularMessages--; _regularBytes -= bytes; }
                _messages--;
                _bytes -= bytes;
            }
        }

        public bool IsDrained
        {
            get
            {
                lock (_gate)
                {
                    return _stopped && _messages == 0;
                }
            }
        }

        public bool HasPending
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count > 0;
                }
            }
        }

        public void Stop(bool discardPending)
        {
            lock (_gate)
            {
                _stopped = true;
                if (discardPending)
                {
                    while (_queue.TryDequeue(out var entry))
                    {
                        if (!entry.IsControl) { _regularMessages--; _regularBytes -= entry.Bytes; }
                        _messages--;
                        _bytes -= entry.Bytes;
                    }
                }
            }
            _wake();
        }
    }
}
