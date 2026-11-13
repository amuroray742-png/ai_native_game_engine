using UnityEngine;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI
{
    internal sealed class UIRoot
    {
        internal GameObject Object { get; }
        internal Transform Staging { get; }
        private readonly Transform[] _layers;
        private readonly GameObject _modalMask;

        internal UIRoot(UIOptions options)
        {
            Object = new GameObject("UnityCore UI", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(Object);
            Object.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = Object.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = options.ReferenceResolution;
            scaler.matchWidthOrHeight = options.MatchWidthOrHeight;
            Transform content = Container("Content", Object.transform);
            _layers = new[]
            {
                Container("HUD", content), Container("Page", content),
                Container("Modal", Object.transform), Container("Overlay", Object.transform)
            };
            var overlay = _layers[(int)UIRole.Overlay].gameObject.AddComponent<CanvasGroup>();
            overlay.interactable = false;
            overlay.blocksRaycasts = false;
            var mask = Container("ModalMask", _layers[(int)UIRole.Modal]);
            _modalMask = mask.gameObject;
            var image = _modalMask.AddComponent<Image>();
            image.color = options.ModalMaskColor;
            image.raycastTarget = true;
            _modalMask.SetActive(false);
            Staging = Container("Staging", Object.transform);
            Staging.gameObject.SetActive(false);
        }

        internal Transform Layer(UIRole role) => _layers[(int)role];

        internal void SetModalTop(UIView top)
        {
            _modalMask.SetActive(top != null);
            if (top != null)
            {
                _modalMask.transform.SetAsLastSibling();
                top.transform.SetAsLastSibling();
            }
        }

        internal void Destroy()
        {
            Object.SetActive(false);
            UnityEngine.Object.Destroy(Object);
        }

        private static RectTransform Container(string name, Transform parent)
        {
            var instance = new GameObject(name, typeof(RectTransform));
            var transform = instance.GetComponent<RectTransform>();
            transform.SetParent(parent, false);
            transform.anchorMin = Vector2.zero;
            transform.anchorMax = Vector2.one;
            transform.offsetMin = Vector2.zero;
            transform.offsetMax = Vector2.zero;
            return transform;
        }
    }
}
