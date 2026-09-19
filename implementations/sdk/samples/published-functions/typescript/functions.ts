export function run(inputs: { marker?: string }, context: unknown) {
  return {
    success: true,
    payload: {
      workerLanguage: "typescript",
      marker: inputs.marker ?? null,
    },
  };
}
