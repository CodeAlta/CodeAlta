/**
 * What turning search by meaning on takes, as the dialog says it before anything is downloaded: the download, the
 * disk of the vectors and the time of the first indexing. The speeds are those of a laptop with four threads, measured
 * for each preset; the first batches give the real speed, which the progress shows afterwards.
 */
export const presetSpeeds: Readonly<Record<string, Readonly<{ chunksPerSecond: number; dimensions: number }>>> = {
  "embeddinggemma-2": { chunksPerSecond: 10, dimensions: 768 },
  "granite-97m-multilingual-r2": { chunksPerSecond: 40, dimensions: 384 },
};

export type MeaningEstimate = Readonly<{ downloadKilobytes: number; diskKilobytes: number; seconds: number }>;

/** The estimate for a model, from the chunks of the index; the runtime is counted when it is not on disk yet. */
export function meaningEstimate(modelId: string, modelKilobytes: number, chunks: number, runtimeInstalled: boolean, runtimeKilobytes = 10 * 1024): MeaningEstimate {
  const speed = presetSpeeds[modelId] ?? { chunksPerSecond: 10, dimensions: 768 };
  return {
    downloadKilobytes: modelKilobytes + (runtimeInstalled ? 0 : runtimeKilobytes),
    // One byte a dimension, a scale and the key of each vector in the index.
    diskKilobytes: Math.ceil(chunks * (speed.dimensions + 24) / 1024),
    seconds: Math.ceil(chunks / speed.chunksPerSecond),
  };
}

/** A duration as the dialog and the progress say it: seconds, minutes, or hours and minutes. */
export function durationParts(seconds: number): Readonly<{ unit: "seconds" | "minutes" | "hours"; value: number; minutes: number }> {
  if (seconds < 90) return { unit: "seconds", value: Math.max(1, Math.round(seconds)), minutes: 0 };
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return { unit: "minutes", value: minutes, minutes: 0 };
  return { unit: "hours", value: Math.floor(minutes / 60), minutes: minutes % 60 };
}

/** A size as the page shows it. */
export function kilobytesText(kilobytes: number, locale: string): string {
  const format = (value: number, digits: number) => value.toLocaleString(locale, { maximumFractionDigits: digits });
  return kilobytes >= 1024 * 1024 ? `${format(kilobytes / (1024 * 1024), 1)} GB` : kilobytes >= 1024 ? `${format(kilobytes / 1024, kilobytes >= 100 * 1024 ? 0 : 1)} MB` : `${format(kilobytes, 0)} KB`;
}
