import assert from 'node:assert/strict';

// Exercise the published C# assembly through its real resource-loading bridge.
export async function verifyNativeCompatibility(page) {
  const results = await page.evaluate(async () => {
    const ui = await import(new URL('uml/viewport.js', document.baseURI));
    const host = [...document.querySelectorAll('[id]')].find(element => element.shadowRoot?.querySelector('.paper'));
    const paper = host.shadowRoot.querySelector('.paper');
    ui.setLocalMode(host.id, true);
    const cases = [
      ['TIM functions', '!function $sum($n,$result=0)\n!if $n==0\n!return $result\n!endif\n!return $sum($n-1,$result+$n)\n!endfunction\nA -> B: Total $sum(5)', 'Total 15'],
      ['JSON and Unicode builtins', '!$data={"name":"straße","list":[1,2]}\nA -> B: %upper($data.name) / %size($data.list)', 'STRASSE / 2'],
      ['Date and color builtins', 'A -> B: %date("yyyy-MM-dd",1700000000,"UTC") / %reverse_hsluv_color("#ff7700")', '2023-11-14'],
      ['Theme and rich text', '!theme carbon-gray\nclass "<size:24><b>Styled</b></size>\\n<color:red>Label</color>" as A', 'Styled'],
      ['OpenIconic and emoji', 'rectangle "<&code> <:grinning:> <#red:grinning:>" as A', 'A'],
      ['C4 element tags', '!include <C4/C4_Container>\nAddElementTag("service", $bgColor="#d5e8d4")\nContainer(web,"Portal","C#","Application", $tags="service")', 'Portal'],
      ['Expanded ArchiMate', '!include <archimate/Archimate>\nMotivation_Goal(goal,"Launch")', 'Launch'],
      ['Azure library', '!include <azure/AzureCommon>\n!include <azure/Compute/AzureVirtualMachine>\nAzureVirtualMachine(vm,"Azure host","VM")', 'Azure host'],
      ['GCP library', '!include <gcp/GCPCommon>\n!include <gcp/Compute/Compute_Engine>\nCompute_Engine(vm,"Google host","VM")', 'Google host'],
      ['AWS alias and bitmap', '!include <awslib/AWSCommon>\n!include <awslib/Compute/EC2>\nEC2(vm,"Amazon host","VM")', 'Amazon host'],
      ['AWS20 library', '!include <awslib20/AWSCommon>\n!include <awslib20/Compute/EC2>\nEC2(vm,"Amazon20 host","VM")', 'Amazon20 host'],
      ['Material alias', '!include <material/account>\nrectangle "<$ma_account> Account" as A', 'Account'],
      ['Font Awesome library', '!include <tupadr3/font-awesome/user>\nrectangle "<$user> User" as A', 'User'],
      ['Logo library', '!include <logos/github>\nrectangle "<$github> GitHub" as A', 'GitHub'],
    ];
    const output = [];
    for (const [name, body, text] of cases) {
      await ui.render(host.id, '@startuml\n' + body + '\n@enduml');
      if (paper.dataset.renderer !== 'managed') throw Error(name + ' selected ' + paper.dataset.renderer);
      if (name === 'OpenIconic and emoji') {
        if (paper.querySelectorAll('[data-sprite]').length !== 3) throw Error('Native icon/emoji artwork missing');
      } else if (!paper.textContent.includes(text)) throw Error(name + ' lost its displayed label');
      if (name.includes('bitmap') && !paper.querySelector('image')) throw Error('AWS inline bitmap missing');
      output.push(name);
    }
    await ui.render(host.id, '@startregex\n!option use_descriptive_names\n!option language fr\n\\d+\\s\\w\n@endregex');
    if (paper.dataset.renderer !== 'managed' || !paper.querySelector('svg')) throw Error('Localized railroad options failed');
    output.push('Localized railroad options');
    await ui.render(host.id, '@startcreole\n= Resources\n|= Name |= Icon |\n| Service | <&cloud> |\n<code>\nvar value = "<literal>";\n</code>\n@endcreole');
    if (paper.dataset.renderer !== 'managed' || paper.querySelectorAll('.creole-cell').length !== 4 || !paper.textContent.includes('<literal>'))
      throw Error('Native Creole table/code layout failed');
    output.push('Creole tables and code');
    return output;
  });
  assert.equal(results.length, 16);
  console.log('Verified native compatibility in published WebAssembly for ' + results.join(', ') + '.');
}
