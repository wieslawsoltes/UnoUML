namespace UnoUML.Core;

public sealed record ToolItem(string Group, string Name, string Snippet, string Families = "UML");
public sealed record DiagramTemplate(string Family, string Name, string Source)
{
    /// <summary>The JavaScript reference engine needs a JVM endpoint for these dialects. The managed renderer handles the templates locally.</summary>
    public bool RequiresReferenceServer => Family is "Files" or "Chen" or "Ditaa" or "Math" or "LaTeX";
    public bool RequiresServer => false;
}

/// <summary>Catalog entries contain ordinary PlantUML. Only the {id} marker is expanded on insertion.</summary>
public static class StudioCatalog
{
    public static IReadOnlyList<ToolItem> Tools { get; } = BuildTools().ToArray();
    public static IEnumerable<ToolItem> ForPage(DiagramPage page) => Tools.Where(tool => Accepts(tool, page));
    public static bool Accepts(ToolItem tool, DiagramPage page)
    {
        bool family = tool.Families == "*" || tool.Families.Split('|').Contains(page.Family, StringComparer.Ordinal)
            || (page.Family == "UML" && tool.Families.Split('|').Any(UmlFamilies.Contains));
        return family && (tool.Families != "C4" || C4Syntax.ToolAvailable(tool, page));
    }
    private static readonly HashSet<string> UmlFamilies = ["Sequence", "Class", "Component", "Deployment", "Use case", "Object", "State", "Activity", "Timing"];

    private static IEnumerable<ToolItem> BuildTools()
    {
        const string structural = "Class|Component|Deployment|Use case|Object";
        foreach (var name in new[] { "Participant", "Actor", "Boundary", "Control", "Entity", "Database", "Collections", "Queue" })
            yield return new("Lifelines", name, name.ToLowerInvariant() + " \"" + name + "\" as {id}", "Sequence");
        foreach (var name in new[] { "Class", "Abstract class", "Interface", "Enum", "Annotation", "Struct", "Exception", "Protocol", "Metaclass", "Stereotype", "Entity" })
            yield return new("Types", name, name.ToLowerInvariant() + " \"" + name + "\" as {id} {\n}", "Class");
        foreach (var name in new[] { "Action", "Actor", "Agent", "Artifact", "Boundary", "Card", "Circle", "Cloud", "Collections", "Component", "Control", "Database", "Entity", "File", "Folder", "Frame", "Hexagon", "Interface", "Label", "Node", "Package", "Person", "Process", "Queue", "Rectangle", "Stack", "Storage", "Usecase" })
            yield return new("Architecture", name, name.ToLowerInvariant() + " \"" + name + "\" as {id}", "Component|Deployment|Use case");
        yield return new("Use cases", "Business actor", "actor/ \"Business actor\" as {id}", "Use case");
        yield return new("Use cases", "Business use case", "usecase/ \"Business use case\" as {id}", "Use case");
        yield return new("Objects", "Object", "object \"Instance\" as {id} {\n  key = value\n}", "Object");
        yield return new("Objects", "Map", "map {id} {\n  key => value\n}", "Object");
        yield return new("Containers", "Package", "package \"Package\" as {id} {\n}", structural);
        yield return new("Containers", "Namespace", "namespace {id} {\n}", "Class");
        yield return new("Sequence", "Alternative", "alt condition\n  ...\nelse otherwise\n  ...\nend", "Sequence");
        foreach (var (name, token) in new[] { ("Optional", "opt"), ("Loop", "loop"), ("Parallel", "par"), ("Critical", "critical"), ("Break", "break"), ("Group", "group") })
            yield return new("Sequence", name, token + " " + name + "\n  ...\nend", "Sequence");
        yield return new("Sequence", "Divider", "== Phase ==", "Sequence");
        yield return new("Sequence", "Delay", "... waiting ...", "Sequence");
        yield return new("Sequence", "Spacing", "|||", "Sequence");
        yield return new("Sequence", "Page break", "newpage Next phase", "Sequence");
        yield return new("Sequence", "Note", "note over {id}\nA note\nend note", "Sequence");
        yield return new("Sequence", "Reference", "ref over {id} : Related interaction", "Sequence");
        yield return new("Sequence", "Activated call", "{id} -> {id} ++ : Work\nreturn Result", "Sequence");
        yield return new("Sequence", "Instance lifecycle", "create participant \"Worker\" as {id}\n\"Creator\" -> {id} ++ : Create\nreturn Ready\n\"Creator\" -> {id} !! : Release", "Sequence");
        yield return new("States", "State", "state \"State\" as {id}", "State");
        foreach (var (name, type) in new[] { ("Choice", "choice"), ("Fork", "fork"), ("Join", "join"), ("Entry point", "entryPoint"), ("Exit point", "exitPoint"), ("Input pin", "inputPin"), ("Output pin", "outputPin") })
            yield return new("States", name, type is "choice" or "fork" or "join" ? "state {id} <<" + type + ">>" :
                "state {id} {\n  state {id}Port <<" + type + ">>\n}", "State");
        yield return new("States", "Composite state", "state {id} {\n  [*] --> Idle\n}", "State");
        yield return new("Activities", "Action", ":New action;", "Activity");
        yield return new("Activities", "Decision", "if (Condition?) then (yes)\n  :Action;\nelse (no)\n  :Alternative;\nendif", "Activity");
        yield return new("Activities", "Parallel paths", "fork\n  :First;\nfork again\n  :Second;\nend fork", "Activity");
        yield return new("Activities", "Repeat", "repeat\n  :Action;\nrepeat while (Again?) is (yes)", "Activity");
        yield return new("Activities", "Loop", "while (Continue?) is (yes)\n  :Action;\nendwhile (no)", "Activity");
        yield return new("Activities", "Partition", "partition {id} {\n  :Action;\n}", "Activity");
        yield return new("Activities", "Swimlane", "|{id}|", "Activity");
        foreach (var name in new[] { "Robust", "Concise", "Binary", "Analog" })
            yield return new("Timing", name, name.ToLowerInvariant() + " \"" + name + "\" as {id}", "Timing");
        yield return new("Timing", "Clock", "clock \"Clock\" as {id} with period 10", "Timing");
        yield return new("Outline", "Branch", "** Topic", "Mind map|WBS");
        yield return new("Outline", "Subtopic", "*** Subtopic", "Mind map|WBS");
        yield return new("Planning", "Task", "[{id}] lasts 3 days", "Gantt");
        yield return new("Planning", "Milestone", "[{id}] happens 2026-01-06", "Gantt");
        yield return new("Charts", "Bar series", "bar \"{id}\" [30, 45, 62, 56] #7464CF", "Chart");
        yield return new("Charts", "Line series", "line \"{id}\" [35, 40, 60, 75] #267E69", "Chart");
        yield return new("Files", "File", "/src/{id}.cs", "Files");
        yield return new("Data", "Value", "value", "JSON");
        yield return new("Chen notation", "Entity", "entity {id} {\n  Id <<key>>\n}", "Chen");
        yield return new("Chen notation", "Weak entity", "entity {id} <<weak>> {\n  Name <<key>>\n}", "Chen");
        yield return new("Chen notation", "Relationship", "relationship {id} {\n}", "Chen");
        yield return new("Chen notation", "Identifying relationship", "relationship {id} <<identifying>> {\n}", "Chen");
        yield return new("ASCII shapes", "Box", "+----------+\n| Box      |\n+----------+", "Ditaa");
        yield return new("ASCII shapes", "Storage", "+----------+\n| {s}      |\n| Storage  |\n+----------+", "Ditaa");
        yield return new("ASCII shapes", "Document", "+----------+\n| {d}      |\n| Document |\n+----------+", "Ditaa");
        yield return new("ASCII shapes", "Connected boxes", "+------+    +------+\n| From +--->| To   |\n+------+    +------+", "Ditaa");
        yield return new("Grammar", "Production", "{id} = \"value\";", "EBNF");
        yield return new("Grammar", "Alternatives", "{id} = \"first\" | \"second\";", "EBNF");
        yield return new("Network", "Network", "network {id} {\n    server [address = \"192.0.2.1\"];\n  }", "Network");
        yield return new("Topology", "Node", "\"{id}\" [label=\"Node\"];", "DOT");
        yield return new("Topology", "Edge", "client -> \"{id}\";", "DOT");
        yield return new("Wireframe", "Button", "[Button]", "Wireframe");
        yield return new("Wireframe", "Text field", "\"Text field                \"", "Wireframe");
        yield return new("Wireframe", "Checkbox", "[X] Checkbox", "Wireframe");
        yield return new("Wireframe", "Dropdown", "^Option^", "Wireframe");
        yield return new("Wireframe", "Radio button", "() Option", "Wireframe");
        yield return new("Wireframe", "Group box", "{^\"Group\"\n    Name | \"Value       \"\n    [Apply]\n  }", "Wireframe");
        yield return new("Wireframe", "Nested form", "{\n    (X) Public | () Private\n    [] Enabled | [] Verified\n  }", "Wireframe");
        yield return new("Wireframe", "Spanning table", "{# Name | First | Second\n    Details | A spanning description | *\n  }", "Wireframe");
        yield return new("Wireframe", "Tree table", "{T# + Name | Value\n    ++ Child | 12\n    +++ Detail | [Open]\n  }", "Wireframe");
        yield return new("Wireframe", "Tabs", "{/ <b>General | Advanced | Saving }", "Wireframe");
        yield return new("Wireframe", "Menu", "{* File | Edit\n    File | New | Open | - | Close }", "Wireframe");
        yield return new("Wireframe", "Scroll area", "{S Text area\n    .\n    \"                    \"\n  }", "Wireframe");
        yield return new("Wireframe", "Open dropdown", "^Basic^^Basic^^Professional^^Enterprise^", "Wireframe");
        yield return new("Data", "Property", "{id}: value", "YAML");
        yield return new("C4", "Person", "Person({id}, \"Person\", \"Description\")", "C4");
        yield return new("C4", "Software system", "System({id}, \"System\", \"Description\")", "C4");
        yield return new("C4", "External system", "System_Ext({id}, \"External system\", \"Description\")", "C4");
        yield return new("C4", "System database", "SystemDb({id}, \"Database\", \"Description\")", "C4");
        yield return new("C4", "System queue", "SystemQueue({id}, \"Queue\", \"Description\")", "C4");
        yield return new("C4 boundaries", "Enterprise boundary", "Enterprise_Boundary({id}, \"Enterprise\") {\n}", "C4");
        yield return new("C4 boundaries", "System boundary", "System_Boundary({id}, \"System\") {\n}", "C4");
        yield return new("C4 boundaries", "Container boundary", "Container_Boundary({id}, \"Container\") {\n}", "C4");
        yield return new("C4 containers", "Container", "Container({id}, \"Container\", \".NET\", \"Description\")", "C4");
        yield return new("C4 containers", "Container database", "ContainerDb({id}, \"Database\", \"PostgreSQL\")", "C4");
        yield return new("C4 containers", "Container queue", "ContainerQueue({id}, \"Queue\", \"AMQP\")", "C4");
        yield return new("C4 components", "Component", "Component({id}, \"Component\", \"C#\", \"Description\")", "C4");
        yield return new("C4 components", "Component database", "ComponentDb({id}, \"Database\", \"SQL\")", "C4");
        yield return new("C4 components", "Component queue", "ComponentQueue({id}, \"Queue\", \"AMQP\")", "C4");
        yield return new("C4 deployment", "Deployment node", "Deployment_Node({id}, \"Node\", \"Linux\") {\n}", "C4");
        foreach (var (name, macro) in new[] { ("Business actor", "Business_Actor"), ("Business role", "Business_Role"), ("Business service", "Business_Service"), ("Business process", "Business_Process"), ("Application component", "Application_Component"), ("Data object", "Application_DataObject"), ("Technology node", "Technology_Node") })
            yield return new("ArchiMate", name, macro + "({id}, \"" + name + "\")", "ArchiMate");
        yield return new("Annotations", "Floating note", "note \"New note\" as {id}", structural + "|State");
        yield return new("Annotations", "Legend", "legend right\n  Diagram key\nendlegend", structural + "|Sequence|State|Activity|Timing");
        yield return new("Annotations", "Comment", "' Comment", structural + "|Sequence|State|Activity|Timing|Gantt|Mind map|WBS");
    }

    private static DiagramTemplate Template(string family, string name, string dialect, string body) =>
        new(family, name, "@start" + dialect + "\n" + body + "\n@end" + dialect + "\n");

    public static IReadOnlyList<DiagramTemplate> Templates { get; } =
    [
        Template("Sequence", "Checkout flow", "uml", """
            title Checkout flow
            hide footbox
            skinparam backgroundColor transparent
            skinparam shadowing false
            skinparam sequence {
              ArrowColor #575D83
              LifeLineBorderColor #9094AF
              ParticipantBorderColor #767CBA
              ParticipantBackgroundColor #EFF0FF
              ParticipantFontColor #232746
              ActorBorderColor #767CBA
              ActorBackgroundColor #EFF0FF
            }
            autonumber
            actor "Shopper" as Shopper
            participant "Storefront" as Store
            participant "Payments" as Pay
            database "Orders" as Orders #DEF5EB

            Shopper -> Store : Place order
            activate Store
            Store -> Pay : Authorize payment
            Pay --> Store : Authorized
            Store -> Orders : Record order
            Orders --> Store : Order number
            Store --> Shopper : Show confirmation
            deactivate Store
            """),
        Template("Class", "Domain model", "uml", "title Order domain\nclass Order {\n  +id : UUID\n  +total() : Money\n}\nclass LineItem {\n  +quantity : int\n}\nOrder \"1\" *-- \"many\" LineItem : contains"),
        Template("Use case", "Customer goals", "uml", "actor \"Customer\" as Customer\nusecase \"Place order\" as Checkout\nCustomer --> Checkout"),
        Template("Activity", "Review process", "uml", "start\n:Prepare review;\nif (Approved?) then (yes)\n  :Publish;\nelse (no)\n  :Revise;\nendif\nstop"),
        Template("State", "Order lifecycle", "uml", "state Draft\nstate Submitted\nstate Complete\n[*] --> Draft\nDraft --> Submitted : submit\nSubmitted --> Complete : fulfill\nComplete --> [*]"),
        Template("Component", "Service boundary", "uml", "component \"Client\" as Client\ncomponent \"API\" as API\ndatabase \"Storage\" as Store\nClient --> API : HTTPS\nAPI --> Store : query"),
        Template("Deployment", "Cloud deployment", "uml", "node \"Edge\" as Edge\nnode \"Application cluster\" as Cluster {\n  artifact \"Worker\" as Worker\n}\nEdge --> Worker"),
        Template("Object", "Order instance", "uml", "object \"order : Order\" as Order {\n  id = 42\n}\nobject \"item : Line\" as Item\nOrder *-- Item"),
        Template("Timing", "Signal trace", "uml", "robust \"Bus\" as Bus\n@0\nBus is Idle\n@10\nBus is Active\n@30\nBus is Idle"),
        Template("Mind map", "Discovery map", "mindmap", "* Product\n** Research\n*** Interviews\n** Design\n*** Prototypes"),
        Template("WBS", "Delivery breakdown", "wbs", "* Delivery\n** Plan\n*** Estimate\n** Implement\n*** Validate"),
        Template("Gantt", "Release plan", "gantt", "Project starts 2026-01-05\n[Design] lasts 3 days\n[Build] starts at [Design]'s end\n[Build] lasts 5 days"),
        Template("Chart", "Weekly throughput", "chart", "h-axis [W1, W2, W3, W4]\nv-axis 0 --> 100\nbar \"Completed\" [32, 51, 60, 75] #7464CF\nlegend right"),
        Template("Files", "Solution tree", "files", "/src/Application.cs\n/src/Models/Order.cs\n/tests/OrderTests.cs"),
        Template("Packet", "Packet header", "packetdiag", "{\n  colwidth = 32\n  0-7: Version\n  8-15: Flags\n  16-31: Length\n}"),
        Template("JSON", "JSON structure", "json", "{\n  \"service\": \"store\",\n  \"regions\": [\"eu\", \"us\"],\n  \"online\": true\n}"),
        Template("YAML", "Service configuration", "yaml", "service: checkout\nregions:\n  - Europe\n  - America"),
        Template("Wireframe", "Sign-in panel", "salt", "{\n  Sign in\n  User | \"Username      \"\n  () Remember me\n  [Submit]\n}"),
        Template("Wireframe", "Settings panel", "salt", "{+\n  {/ <b>Account | Appearance | Advanced }\n  {^\"Account\"\n    Name | \"Ada           \"\n    Access | { (X) Public | () Private\n               [X] Notifications | [] Digest }\n    Plan | ^Professional^\n  }\n  {# Property | Value\n    Locale | Europe\n    Notes | Configured for this account }\n  [Cancel] | [Apply]\n}"),
        Template("Network", "Office network", "nwdiag", "nwdiag {\n  network office {\n    address = \"192.0.2.0/24\";\n    client [address = \"192.0.2.10\"];\n    server [address = \"192.0.2.20\"];\n  }\n}"),
        Template("EBNF", "Identifier grammar", "ebnf", "digit = \"0\" | \"1\";\nsequence = digit, { digit };"),
        Template("Regex", "Regular expression", "regex", "[A-Z][a-z]+"),
        Template("DOT", "Directed graph", "dot", "digraph G {\n  rankdir=LR;\n  client -> server;\n  server -> storage;\n}"),
        Template("C4", "System context", "uml", "!include <C4/C4_Context>\nPerson(user, \"Customer\")\nSystem(store, \"Storefront\")\nRel(user, store, \"Shops\")"),
        Template("C4", "Container architecture", "uml", "!include <C4/C4_Container>\nPerson(user, \"Customer\")\nSystem_Boundary(store, \"Storefront\") {\n  Container(web, \"Web application\", \"ASP.NET Core\")\n  ContainerDb(data, \"Orders\", \"PostgreSQL\")\n}\nRel(user, web, \"Shops\", \"HTTPS\")\nRel(web, data, \"Stores orders\", \"SQL\")"),
        Template("C4", "Component architecture", "uml", "!include <C4/C4_Component>\nContainer_Boundary(api, \"Orders API\") {\n  Component(controller, \"Controller\", \"ASP.NET Core\")\n  Component(service, \"Order service\", \"C#\")\n  ComponentDb(repository, \"Repository\", \"Entity Framework\")\n}\nRel(controller, service, \"Creates order\")\nRel(service, repository, \"Persists\")"),
        Template("C4", "Deployment architecture", "uml", "!include <C4/C4_Deployment>\nDeployment_Node(region, \"Region\", \"Datacenter\") {\n  Deployment_Node(host, \"Application host\", \"Linux\") {\n    Container(api, \"Orders API\", \".NET\")\n  }\n  Deployment_Node(database, \"Database host\", \"Linux\") {\n    ContainerDb(data, \"Orders\", \"PostgreSQL\")\n  }\n}\nRel(api, data, \"Reads and writes\", \"SQL\")"),
        Template("ArchiMate", "Business capability", "uml", "!include <archimate/Archimate>\nBusiness_Role(customer, \"Customer\")\nBusiness_Process(order, \"Place order\")\nRel_Assignment(customer, order)"),
        Template("Chen", "Entity relationship", "chen", "entity CUSTOMER {\n  Id <<key>>\n  Name\n}\nentity ORDER {\n  Number <<key>>\n}\nrelationship PLACES {\n}\nPLACES -1- CUSTOMER\nPLACES -N- ORDER"),
        Template("Ditaa", "ASCII architecture", "ditaa", "+--------+     +-------+\n| Client +---->| Store |\n+--------+     +-------+"),
        Template("Math", "Equation", "math", "E = mc^2"),
        Template("LaTeX", "Typeset equation", "latex", "\\frac{a}{b}"),
        Template("Creole", "Formatted notes", "creole", "= System notes\n* First item\n* **Important** item")
    ];

    internal static string FamilyName(string dialect) => dialect switch
    {
        "mindmap" => "Mind map", "wbs" => "WBS", "gantt" => "Gantt", "chronology" => "Chronology",
        "json" => "JSON", "yaml" => "YAML", "salt" => "Wireframe", "nwdiag" => "Network",
        "ebnf" => "EBNF", "regex" => "Regex", "dot" => "DOT", "chen" => "Chen", "ditaa" => "Ditaa",
        "chart" => "Chart", "files" => "Files", "packetdiag" => "Packet", "math" => "Math", "latex" => "LaTeX", "creole" => "Creole", _ => dialect
    };
}
