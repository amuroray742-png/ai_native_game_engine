using UnityEngine;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridBoardArt : ScriptableObject
    {
        [SerializeField] private Sprite square, tower, diamond;
        [SerializeField] private Material material;

        internal void Validate()
        {
            if (square == null || tower == null || diamond == null || material == null)
            {
                throw new System.InvalidOperationException("Grid board artwork requires three sprites and a material.");
            }
        }

        internal Sprite Square => square;
        internal Sprite Tower => tower;
        internal Sprite Diamond => diamond;
        internal Material Material => material;
    }
}
