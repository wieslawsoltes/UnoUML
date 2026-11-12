import {mkdir, writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';

// Failure-only diagnostics for Uno's native semantic tree. This never changes a
// failed test into a pass and is not imported by the application. The optional
// callbacks are feature-detected because they belong to the framework runtime.
export async function diagnoseNativeAccessibility(page, artifactDir = process.env.SCREENSHOT_DIR) {
  const report = await page.evaluate(async () => {
    const accessibility = globalThis.Uno?.UI?.Runtime?.Accessibility;
    const phases = [];
    const capture = (phase, error) => {
      const root = document.getElementById('uno-semantics-root');
      const focus = [];
      for (let element = document.activeElement; element && focus.length < 8; element = element.shadowRoot?.activeElement) {
        focus.push({ tag: element.localName, id: element.id,
          role: element.getAttribute('role'), name: element.getAttribute('aria-label'),
          controls: element.getAttribute('aria-controls'), activeDescendant: element.getAttribute('aria-activedescendant') });
      }
      let autoEnable;
      try { autoEnable = accessibility?.managedIsAutoEnableAccessibility?.(); }
      catch (failure) { autoEnable = String(failure); }
      phases.push({phase, error, autoEnable, rootPresent: !!root,
        documentFocused: document.hasFocus(), focus,
        children: root?.childElementCount ?? 0,
        buttons: root?.querySelectorAll('button,[role="button"]').length ?? 0,
        html: root?.outerHTML.slice(0, 256 * 1024) ?? '',
        callbacks: Object.fromEntries(['managedEnableAccessibility', 'managedDisableAccessibility', 'disableAccessibility']
          .map(name => [name, typeof accessibility?.[name]]))});
    };
    const settle = () => new Promise(resolve => setTimeout(resolve, 250));
    const invoke = async (phase, name) => {
      try {
        if (typeof accessibility?.[name] !== 'function') throw new Error(name + ' is unavailable');
        accessibility[name]();
        await settle();
        capture(phase);
      } catch (error) { capture(phase, String(error)); }
    };
    capture('failure');
    await invoke('enable-after-startup', 'managedEnableAccessibility');
    await invoke('disable', 'disableAccessibility');
    await invoke('enable-after-reset', 'managedEnableAccessibility');
    return {browser: navigator.userAgent, phases};
  }).catch(error => ({diagnosticError: String(error)}));
  console.log('Native accessibility failure diagnostics:', JSON.stringify(report.phases?.map(({html, ...phase}) => phase) ?? report));
  if (artifactDir) {
    await mkdir(artifactDir, {recursive: true});
    await writeFile(resolve(artifactDir, 'native-accessibility-failure.json'), JSON.stringify(report, null, 2));
  }
}
