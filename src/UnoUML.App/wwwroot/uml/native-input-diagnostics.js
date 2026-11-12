// Readers are registered only while their public TextBox is loaded. Nothing is
// sampled until an opt-in diagnostic explicitly requests the current snapshot.
const readers = new Map();

export function registerTextBoxDiagnostics(automationId, generation, read) {
  if (typeof automationId !== 'string' || !automationId || !Number.isInteger(generation)
      || generation < 1 || typeof read !== 'function') return false;
  const previous = readers.get(automationId);
  if (previous && previous.generation >= generation) return false;
  readers.set(automationId, { generation, read });
  return true;
}

export function unregisterTextBoxDiagnostics(automationId, generation) {
  const entry = readers.get(automationId);
  if (!entry || entry.generation !== generation) return false;
  readers.delete(automationId);
  return true;
}

export function readTextBoxDiagnostics(automationId) {
  const entry = readers.get(automationId);
  if (!entry) return null;
  try { return JSON.parse(entry.read()); }
  catch (error) { return { error: String(error?.message ?? error) }; }
}
