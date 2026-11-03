// XAML owns names and enabled/read-only/expanded state. The native TextBox input
// is a template child, so observe only that control's child-list replacements.
// No document-wide observer or render/layout polling is needed.
const bindings = new Map();

function attribute(element, name, value) {
  if (value == null) {
    if (element.hasAttribute(name)) element.removeAttribute(name);
  } else if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

function apply(binding) {
  const { element, role, name, enabled, readOnly, expanded } = binding;
  const target = role === 'textbox' ? element.querySelector('textarea,input') : element;
  if (!target) return;
  if (role === 'textbox') {
    // The focusable input is the accessible edit control, not its XAML wrapper.
    attribute(element, 'role', null);
    attribute(element, 'aria-label', null);
    attribute(target, 'aria-readonly', String(readOnly));
  }
  attribute(target, 'role', role);
  attribute(target, 'aria-label', name || null);
  attribute(target, 'aria-disabled', String(!enabled));
  if (role === 'combobox') {
    attribute(target, 'aria-expanded', String(expanded));
    attribute(target, 'aria-haspopup', 'listbox');
  }
  if (role === 'dialog') attribute(target, 'aria-modal', 'true');
}

export function configureAccessibility(id, role, name, enabled, readOnly, expanded) {
  const element = document.getElementById(id);
  if (!element) return;
  let binding = bindings.get(id);
  if (binding?.element !== element || binding?.role !== role) {
    removeAccessibility(id);
    binding = { element, role };
    bindings.set(id, binding);
    if (role === 'textbox') {
      binding.observer = new MutationObserver(() => apply(binding));
      binding.observer.observe(element, { childList: true, subtree: true });
    }
  }
  Object.assign(binding, { name, enabled, readOnly, expanded });
  apply(binding);
}

export function removeAccessibility(id) {
  bindings.get(id)?.observer?.disconnect();
  bindings.delete(id);
}
