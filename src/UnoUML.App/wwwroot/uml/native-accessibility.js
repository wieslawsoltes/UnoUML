// Uno 7.0.32 FocusTrap hides every semantic node outside the ContentDialog,
// including the Popup/application ancestors that contain the dialog itself.
// Repair that ancestry after ContentDialog.Opened using public AutomationId and
// ARIA contracts. Uno retains background isolation, Tab trapping and restoration.
// See unoplatform/uno tag 7.0.32, ts/Runtime/FocusTrap.ts activateFocusTrap.
export function restoreDialogAncestry(automationId) {
  if (typeof automationId !== 'string' || !automationId) return 0;
  const root = document.getElementById('uno-semantics-root');
  const dialog = root?.querySelector('[xamlautomationid="' + CSS.escape(automationId) + '"]');
  if (!dialog || dialog.getAttribute('role') !== 'dialog' || dialog.getAttribute('aria-modal') !== 'true') return 0;
  let changed = 0;
  for (let ancestor = dialog.parentElement; ancestor && ancestor !== root; ancestor = ancestor.parentElement) {
    if (ancestor.id.startsWith('uno-semantics-') && ancestor.getAttribute('aria-hidden') === 'true') {
      ancestor.removeAttribute('aria-hidden');
      changed++;
    }
  }
  return changed;
}

const activationBoundaries = new WeakMap();

// Uno 7.0.32 removes its enable control and synchronizes native focus during
// keydown, then forwards that same event from document to the newly focused
// XAML control. Keep this one public control's activation within its existing
// click handler, consuming the claimed physical key through repeat and keyup.
export function installAccessibilityActivationBoundary(doc = document) {
  const existing = activationBoundaries.get(doc);
  if (existing) return existing;
  let claimedKey = null;
  const key = event => event.code || event.key;
  const consume = event => { event.preventDefault(); event.stopPropagation(); };
  const down = event => {
    if (claimedKey !== null && key(event) === claimedKey) { consume(event); return; }
    const target = doc.getElementById('uno-enable-accessibility');
    if (!target || event.target !== target || target.getAttribute('role') !== 'button'
        || (event.key !== 'Enter' && event.key !== ' ')) return;
    claimedKey = key(event);
    consume(event);
    target.click();
  };
  const up = event => {
    if (claimedKey !== null && key(event) === claimedKey) { claimedKey = null; consume(event); }
  };
  const blur = () => { claimedKey = null; };
  doc.addEventListener('keydown', down, true);
  doc.addEventListener('keyup', up, true);
  doc.defaultView?.addEventListener('blur', blur);
  const dispose = () => {
    if (activationBoundaries.get(doc) !== dispose) return;
    doc.removeEventListener('keydown', down, true);
    doc.removeEventListener('keyup', up, true);
    doc.defaultView?.removeEventListener('blur', blur);
    claimedKey = null;
    activationBoundaries.delete(doc);
  };
  activationBoundaries.set(doc, dispose);
  return dispose;
}
