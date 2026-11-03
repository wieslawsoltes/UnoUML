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
    const documents = [
      ['Single-execution C4 macros', '@startuml\n!include <C4/C4_Context>\n!$count=0\n!function $next()\n!global $count=$count+1\n!return $count\n!endfunction\n!$person="User "+$next()\n!$application="App "+$next()\nPerson(user,$person)\nSystem(app,$application)\nRel(user,app,"Uses")\n@enduml', 'App 2', '[data-c4-alias]'],
      ['Inline formula typesetting', '@startuml\nclass "<math>sum_(i=0)^n x_i</math>" as A\n@enduml', '', '[data-sprite^="$math/"]'],
      ['Measured activity styles', '@startuml\n<style>\nactivityDiagram { activity { FontSize 24; Padding 10; MaximumWidth 180 } diamond { FontSize 22 } }\n</style>\nstart\n#lightblue:**Prepare** review\nwith details; <<procedure>>\nif (Ready?) then (yes)\n:Publish;\nelse (no)\n:Revise;\nendif\nstop\n@enduml', 'Prepare', '[data-activity-shape="procedure"]'],
      ['Measured timing styles', '@startuml\n<style>\ntimingDiagram { concise { FontSize 22; Padding 4 } arrow { FontSize 24; MaximumWidth 150 } }\n</style>\nconcise "**Client**" as C\n@0\nC is Waiting\n@10\nC is Received\nC -> C@+3 : **Large**\\nmessage caption\nlegend\nSummary\nendlegend\n@enduml', 'Received', '.timing-message'],
      ['DOT clusters and HTML ports', '@startdot\ndigraph { rankdir=LR; subgraph cluster_one { label=Group; a[shape=plain label=<<TABLE BORDER="1" CELLBORDER="1" CELLSPACING="0"><TR><TD PORT="out"><B>First</B></TD></TR></TABLE>>]; } b[shape=record label="<in> Second|End"]; a:out:e->b:in:w[label=Route arrowhead=odiamond]; }\n@enddot', 'Route', '[data-dot-kind="cluster"]'],
      ['Nested YAML and measured styles', '@startyaml\n<style>\nyamlDiagram { node { FontSize 22; Padding 8; MaximumWidth 180 } }\n</style>\n- name: First\n  values: [same, same, 12]\n  note: >-\n    folded\n    text\n@endyaml', 'folded text', '[data-edit-kind="yaml-block"]'],
      ['JSON highlighting and rich labels', '@startjson\n#highlight "nested" / "label"\n{"nested":{"label":"**Rich** <math>sqrt x</math>","number":12}}\n@endjson', 'Rich', '[data-style-name="node highlight"]'],
      ['Measured Salt widgets', '@startsalt\n<style>\nbutton { FontSize 24; Padding 9 }\n</style>\n{\n[**Submit**] | "Account       "\n}\n@endsalt', 'Submit', '[data-edit-kind="salt-text"]'],
      ['Nested Salt controls and popups', '@startsalt\n{+\n{/ General | Advanced }\n{^"Account"\nAccess | { [ ] Enabled | (X) Public }\nPlan | ^Basic^^Professional^^Enterprise^\n}\n{T# + Region | Count\n++ Europe | 12\n}\n{SI Notes\n.\n}\n}\n@endsalt', 'Enterprise', '[data-edit-kind="salt-state"]'],
      ['Timing anchors and decorations', '@startuml\nmanual time-axis\nscale 1 as 40 pixels\nconcise "Data bus" as Bus\nbinary "Enabled" as Enable\n@0 as :start\nBus is Idle\nEnable is low\n@5 as :ready\nBus is "Data ready"\nEnable is high\nBus@:start <-> @:ready : Transfer time\nhighlight :start to :ready #Gold;line:DimGrey : Payload window\nnote top of Bus : Ready for transfer\n@enduml', 'Payload window', '.timing-constraint'],
      ['YAML folded literals and directive boundaries', '@startyaml\nvalue:\n  &base literal\n  *base\n  !!str 42\n!custom: tagged\n@endyaml', '&base literal *base !!str 42', '[data-edit-kind="yaml-folded-text"]'],
      ['Extended TeX layout', '@startlatex\n\\begin{aligned} x &= \\textcolor{red}{\\frac{a}{b}} \\\\ y &= \\overbrace{a+b}^{n} \\end{aligned}\n@endlatex', 'a', 'svg'],
    ];
    for (const [name, source, expectedText, selector] of documents) {
      await ui.render(host.id, source);
      if (paper.dataset.renderer !== 'managed') throw Error(name + ' selected ' + paper.dataset.renderer);
      if (!paper.textContent.includes(expectedText) || !paper.querySelector(selector))
        throw Error(name + ' lost its label or native SVG structure');
      output.push(name);
    }
    return output;
  });
  assert.equal(results.length, 28);
  console.log('Verified native compatibility in published WebAssembly for ' + results.join(', ') + '.');
}
