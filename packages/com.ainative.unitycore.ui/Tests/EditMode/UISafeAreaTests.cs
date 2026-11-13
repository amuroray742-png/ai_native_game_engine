using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UISafeAreaTests
    {
        [Test]
        public void PortraitLandscapeAndRestoredFullScreenNormalize()
        {
            Assert.AreEqual(Rect.MinMaxRect(0.05f, 0.1f, 0.95f, 0.9f),
                UISafeArea.Normalize(1000, 2000, Rect.MinMaxRect(50, 200, 950, 1800)));
            Assert.AreEqual(Rect.MinMaxRect(0.1f, 0.05f, 0.9f, 0.95f),
                UISafeArea.Normalize(2000, 1000, Rect.MinMaxRect(200, 50, 1800, 950)));
            Assert.AreEqual(new Rect(0, 0, 1, 1), UISafeArea.Normalize(2000, 1000, new Rect(0, 0, 2000, 1000)));
        }

        [TestCase(0, 100)]
        [TestCase(100, 0)]
        [TestCase(0, 0)]
        public void ZeroDimensionsUseFiniteFullScreen(int width, int height)
        {
            Assert.AreEqual(new Rect(0, 0, 1, 1), UISafeArea.Normalize(width, height, new Rect(0, 0, 10, 10)));
        }

        [Test]
        public void ScreenBoundsClampSafeArea()
        {
            Assert.AreEqual(new Rect(0, 0, 1, 1), UISafeArea.Normalize(100, 100, new Rect(-10, -20, 150, 160)));
        }

        [Test]
        public void ApplyingSafeAreaChangesOnlyContentAnchors()
        {
            var root = new GameObject("view", typeof(RectTransform), typeof(UIView));
            try
            {
                var safe = new GameObject("SafeArea", typeof(RectTransform)).GetComponent<RectTransform>();
                safe.SetParent(root.transform, false);
                var visual = new GameObject("Visual", typeof(RectTransform)).GetComponent<RectTransform>();
                visual.SetParent(safe, false);
                visual.anchoredPosition = new Vector2(7, 9);
                var serialized = new SerializedObject(root.GetComponent<UIView>());
                serialized.FindProperty("_safeArea").objectReferenceValue = safe;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var rootRect = root.GetComponent<RectTransform>();
                Vector2 before = rootRect.anchorMin;
                UISafeArea.Apply(root.GetComponent<UIView>(), Rect.MinMaxRect(0.1f, 0.2f, 0.9f, 0.8f));
                Assert.AreEqual(new Vector2(0.1f, 0.2f), safe.anchorMin);
                Assert.AreEqual(new Vector2(0.9f, 0.8f), safe.anchorMax);
                Assert.AreEqual(Vector2.zero, safe.offsetMin);
                Assert.AreEqual(Vector2.zero, safe.offsetMax);
                Assert.AreEqual(before, rootRect.anchorMin);
                Assert.AreEqual(new Vector2(7, 9), visual.anchoredPosition);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
