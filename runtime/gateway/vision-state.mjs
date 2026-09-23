// The installer/operator declares a verified backend capability. No process discovery or shell calls.
export async function visionReady() {
  return process.env.QWEN_VISION_READY === '1';
}
