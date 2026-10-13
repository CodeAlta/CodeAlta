/**
 * The name of the audio worklet of the capture in the built application, beside its entry document. The build
 * emits `pcm-worklet.js` there (vite.config.ts) and the capture asks for it by this name, so the two agree.
 */
export const workletFile = "pcm-worklet.js";

/** The name the worklet registers its processor under, and the capture creates its node with. */
export const workletProcessor = "codealta-pcm";
