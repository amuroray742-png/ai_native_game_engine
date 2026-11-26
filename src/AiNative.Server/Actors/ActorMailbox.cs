using System;
using System.Collections.Generic;

namespace AiNative.Server.Actors
{
    internal sealed class ActorMailbox<T> where T : notnull
    {
        private readonly object _gate = new object();
        private readonly Queue<(T Message, int Bytes)> _queue = new Queue<(T, int)>();
        private readonly int _maxMessages;
        private readonly long _maxBytes;
        private readonly Action _wake;
        private int _messages;
        private long _bytes;
        private bool _stopped;

        public ActorMailbox(int maxMessages, long maxBytes, Action wake)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessages);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            _maxMessages = maxMessages;
            _maxBytes = maxBytes;
            _wake = wake;
        }

        public PostResult TryPost(T message, int byteCount)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
            lock (_gate)
            {
                if (_stopped)
                {
                    return PostResult.Stopped;
                }
                if (_messages == _maxMessages || byteCount > _maxBytes - _bytes)
                {
                    return PostResult.Full;
                }
                _queue.Enqueue((message, byteCount));
                _messages++;
                _bytes += byteCount;
            }
            _wake();
            return PostResult.Accepted;
        }

        public bool TryTake(out (T Message, int Bytes) entry)
        {
            lock (_gate)
            {
                return _queue.TryDequeue(out entry);
            }
        }

        public void Release(int bytes)
        {
            lock (_gate)
            {
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
                        _messages--;
                        _bytes -= entry.Bytes;
                    }
                }
            }
            _wake();
        }
    }
}
