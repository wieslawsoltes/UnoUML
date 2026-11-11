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
