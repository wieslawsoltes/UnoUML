export { beginDialogScope, activateDialogScope, endDialogScope } from './native-dialogs.js';
export { beginComboScope, updateComboScope, endComboScope, focusComboScope } from './native-combos.js';

const keyboardBoundaries = new WeakMap();

// Uno 7.0.32 removes its enable control and synchronizes native focus during
// keydown, then forwards that same event from document to the newly focused
// XAML control. Keep this one public control's activation within its existing
// click handler, consuming the claimed physical key through repeat and keyup.
export function installAccessibilityKeyboardBoundary(doc = document) {
  const existing = keyboardBoundaries.get(doc);
  if (existing) return existing;
  const claimedKeys = new Map();
  const semanticActions = new WeakMap();
  const key = event => event.code || event.key;
  const consume = event => { event.preventDefault(); event.stopPropagation(); };
  const down = event => {
    const control = event.target;
    const role = control?.closest?.('#uno-semantics-root') ? control.getAttribute('role') : null;
    const navigation = role === 'menuitem' && (event.key === 'ArrowUp' || event.key === 'ArrowDown');
    const claimed = claimedKeys.get(key(event));
    if (claimed && !(claimed === 'menu-navigation' && navigation)) { consume(event); return; }
    const target = doc.getElementById('uno-enable-accessibility');
    if (target && event.target === target && target.getAttribute('role') === 'button'
        && (event.key === 'Enter' || event.key === ' ')) {
      claimedKeys.set(key(event), 'activation');
      consume(event);
      target.click();
      return;
    }

    // SemanticElements.ts handles these keys at the target before native
    // document forwarding. Capture eligibility now: invoking a menu item can
    // remove that target and open/focus a dialog during the same dispatch.
    const invoke = event.key === 'Enter' || event.key === ' ';
    // MenuItem Escape only focuses its parent; native dispatch still closes the
    // menu. ComboBox Escape directly collapses and is a complete semantic action.
    if ((role === 'combobox' && (invoke || event.key === 'Escape' || (event.key === 'ArrowDown' && event.altKey)))
        || (role === 'menuitem' && (invoke || ['ArrowUp', 'ArrowDown', 'ArrowRight'].includes(event.key))))
      semanticActions.set(event, navigation ? 'menu-navigation' : 'action');
  };
  const handled = event => {
    const action = semanticActions.get(event);
    semanticActions.delete(event);
    if (!action || !event.defaultPrevented) return;
    claimedKeys.set(key(event), action);
    // Installed before host startup, hence before BrowserKeyboardInputSource's
    // document listener. Preserve the target's existing public action exactly.
    event.stopImmediatePropagation();
  };
  const up = event => {
    if (claimedKeys.delete(key(event))) consume(event);
  };
  const blur = () => claimedKeys.clear();
  doc.addEventListener('keydown', down, true);
  doc.addEventListener('keydown', handled);
  doc.addEventListener('keyup', up, true);
  doc.defaultView?.addEventListener('blur', blur);
  const dispose = () => {
    if (keyboardBoundaries.get(doc) !== dispose) return;
    doc.removeEventListener('keydown', down, true);
    doc.removeEventListener('keydown', handled);
    doc.removeEventListener('keyup', up, true);
    doc.defaultView?.removeEventListener('blur', blur);
    claimedKeys.clear();
    keyboardBoundaries.delete(doc);
  };
  keyboardBoundaries.set(doc, dispose);
  return dispose;
}
