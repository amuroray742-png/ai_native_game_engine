# Owned resources

The complete runnable sample is `examples/unity/AssetsDemo` in this repository. It includes an explicit application composition root, A/B prefabs sharing one material and texture, a ScriptableObject loaded only from a bundle, an audio clip, a sprite subasset and an additive scene.

Use `AssetsDemoSetup.Create` to regenerate fixtures through Unity APIs, then `AssetsDemoSetup.BuildContent` to create a fresh StreamingAssets/AiNativeAssets directory. Open Assets/Bootstrap.unity and enter Play. The sample exercises ownership and displays a PASS or error message. Run its player with `-assetsSmoke -assetsResult <absolute-json-path>` for an exit-code-based smoke test.
