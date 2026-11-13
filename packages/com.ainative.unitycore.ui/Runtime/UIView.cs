using UnityEngine;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI
{
    public class UIView : MonoBehaviour
    {
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private RectTransform _safeArea;
        [SerializeField] private RectTransform _visual;
        [SerializeField] private Selectable _defaultSelection;

        public CanvasGroup CanvasGroup => _canvasGroup;
        public RectTransform SafeArea => _safeArea;
        public RectTransform Visual => _visual;
        public Selectable DefaultSelection => _defaultSelection;
    }
}
