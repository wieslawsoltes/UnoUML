// Software adapters exercise the real WebGPU API and shaders on GPU-less CI.
// They are a correctness target, not evidence of discrete-GPU performance.
export function graphicsLaunchOptions(required = process.env.UNOUML_REQUIRE_WEBGPU === '1') {
  const software = required && process.platform === 'linux' && process.env.UNOUML_GPU_DRIVER !== 'hardware';
  return {
    executablePath: process.env.CHROMIUM_PATH || undefined,
    headless: process.env.UNOUML_HEADED !== '1',
    args: required ? [
      '--enable-unsafe-webgpu',
      ...(software ? [
        '--use-angle=swiftshader',
        '--enable-unsafe-swiftshader',
        '--use-vulkan=swiftshader',
        '--enable-features=Vulkan'
      ] : [])
    ] : []
  };
}
