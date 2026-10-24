# GpuTextureAtlas

This package provides GPU ASTC and BC7 encoders and dynamic compressed atlases.
Use Unity 6000.7.0b5 or later. Other Unity versions are not tested.

See the [project README](https://github.com/ittodo/GpuTextureAtlas#readme) for the API, limits, platform tests, and license notices.

Import **Dynamic Atlas** from the Package Manager Samples list. Then select **Tools > GpuTextureAtlas > Create sample scene**. Save the scene in your project and enter Play Mode. The sample uses generated RGBA inputs if the input list is empty. You can assign imported textures with Read/Write disabled.

The core package does not require Addressables or URP. Use URP 2D Renderer for SpriteMask. Keep source loading handles until each atlas task finishes, including a canceled or failed task.
