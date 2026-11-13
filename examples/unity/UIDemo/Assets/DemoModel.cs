using System;
using AiNative.UnityCore.Assets;
using UnityEngine;

namespace AiNative.UIDemo
{
    public sealed class DemoModel : IDisposable
    {
        private AssetLease<Texture2D> _image;
        public int Session { get; }
        public bool IsDisposed { get; private set; }
        public bool FailNextUnbind { get; set; }
        public Texture2D Image => _image == null ? null : _image.Value;
        public DemoModel(int session, AssetLease<Texture2D> image) { Session = session; _image = image; }
        public void ReleaseImage() { _image?.Dispose(); _image = null; }
        public void Dispose() { ReleaseImage(); IsDisposed = true; }
    }
}

