// XAML owns names and enabled/read-only/expanded state. The native TextBox input
// is a template child, so observe only that control's child-list replacements.
// No document-wide observer or render/layout polling is needed.
const bindings = new Map();

function attribute(binding, element, name, value) {
  let attributes = binding.attributes.get(element);
  if (!attributes) binding.attributes.set(element, attributes = new Map());
  const current = element.getAttribute(name);
  let previous = attributes.get(name);
  if (!previous) attributes.set(name, previous = { original: current, applied: current });
  // A template/framework update can replace an attribute while attached. Retain
  // that newer native value when this registration is later removed.
  if (current !== previous.applied) previous.original = current;
  previous.applied = value;
  if (value == null) {
    if (current != null) element.removeAttribute(name);
  } else if (current !== value) element.setAttribute(name, value);
}

function restore(binding, element) {
  const attributes = binding.attributes.get(element);
  if (!attributes) return;
  for (const [name, { original, applied }] of attributes) {
    if (element.getAttribute(name) !== applied) continue;
    if (original == null) element.removeAttribute(name);
    else element.setAttribute(name, original);
  }
  binding.attributes.delete(element);
}

function applyPopup(binding) {
  const { element, popup, name, enabled } = binding;
  attribute(binding, element, 'aria-controls', popup?.id || null);
  // Uno portals ComboBox popups beside ContentDialog. Own the list so it remains
  // within the modal's accessible subtree without moving its actual DOM.
  attribute(binding, element, 'aria-owns', popup?.id || null);
  if (!popup) return;
  attribute(binding, popup, 'role', 'listbox');
  attribute(binding, popup, 'aria-label', name || null);
  attribute(binding, popup, 'aria-disabled', String(!enabled));
}

function apply(binding) {
  const { element, role, name, enabled, readOnly, expanded, state, position, count } = binding;
  const target = role === 'textbox' ? element.querySelector('textarea,input') : element;
  if (binding.target !== target) {
    if (binding.target && binding.target !== element) restore(binding, binding.target);
    binding.target = target;
  }
  if (!target) return;
  if (role === 'textbox') {
    // The focusable input is the accessible edit control, not its XAML wrapper.
    attribute(binding, element, 'role', null);
    attribute(binding, element, 'aria-label', null);
    attribute(binding, target, 'aria-readonly', String(readOnly));
  }
  attribute(binding, target, 'role', role);
  attribute(binding, target, 'aria-label', name || null);
  attribute(binding, target, 'aria-disabled', String(!enabled));
  if (role === 'combobox') {
    attribute(binding, target, 'aria-expanded', String(expanded));
    attribute(binding, target, 'aria-haspopup', 'listbox');
    applyPopup(binding);
  }
  if (role === 'option') {
    attribute(binding, target, 'aria-selected', state || 'false');
    const indexed = position > 0 && position <= count;
    attribute(binding, target, 'aria-posinset', indexed ? String(position) : null);
    attribute(binding, target, 'aria-setsize', indexed ? String(count) : null);
  }
  if (role === 'checkbox' || role === 'radio' || role === 'switch')
    attribute(binding, target, 'aria-checked', state || 'false');
  if (role === 'button' && state) attribute(binding, target, 'aria-pressed', state);
  if (role === 'dialog') attribute(binding, target, 'aria-modal', 'true');
}

export function configureAccessibility(id, role, name, enabled, readOnly, expanded, state = '', position = 0, count = 0) {
  const element = document.getElementById(id);
  if (!element) return;
  let binding = bindings.get(id);
  if (binding?.element !== element || binding?.role !== role) {
    removeAccessibility(id);
    binding = { element, role, attributes: new Map() };
    bindings.set(id, binding);
    if (role === 'textbox') {
      binding.observer = new MutationObserver(() => apply(binding));
      binding.observer.observe(element, { childList: true, subtree: true });
    }
  }
  Object.assign(binding, { name, enabled, readOnly, expanded, state, position, count });
  apply(binding);
}

export function configureAccessibilityPopup(id, panelId) {
  const binding = bindings.get(id);
  if (!binding || binding.role !== 'combobox') return;
  const popup = panelId ? document.getElementById(panelId) : null;
  if (binding.popup !== popup) {
    if (binding.popup) restore(binding, binding.popup);
    binding.popup = popup;
  }
  applyPopup(binding);
}

export function removeAccessibility(id) {
  const binding = bindings.get(id);
  if (!binding) return;
  binding.observer?.disconnect();
  for (const element of binding.attributes.keys()) restore(binding, element);
  bindings.delete(id);
}
