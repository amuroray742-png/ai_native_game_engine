using System;
using UnityEngine;

namespace AiNative.UnityCore.UI
{
    public readonly struct UITransitionOptions
    {
        public float DurationSeconds { get; }
        public bool Fade { get; }
        public Vector2 EnterOffset { get; }
        public float EnterScale { get; }

        public UITransitionOptions(float durationSeconds, bool fade, Vector2 enterOffset, float enterScale)
        {
            if (!IsFinite(durationSeconds) || durationSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            }
            if (!IsFinite(enterOffset.x) || !IsFinite(enterOffset.y))
            {
                throw new ArgumentOutOfRangeException(nameof(enterOffset));
            }
            if (!IsFinite(enterScale) || enterScale <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(enterScale));
            }
            DurationSeconds = durationSeconds;
            Fade = fade;
            EnterOffset = enterOffset;
            EnterScale = enterScale;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
