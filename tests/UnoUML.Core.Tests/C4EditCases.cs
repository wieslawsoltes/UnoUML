using UnoUML.Core;

internal static class C4EditCases
{
    internal static void Run(Action<bool, string> check)
    {
        const string source = "@startuml\r\n!include <C4/C4_Component>\r\nPerson ( user , \"Customer\" ) ' Keep caller\r\nSystem_Boundary(site, \"Booking\", $tags = \"sales\", $link = \"https://example.test\") {\r\n  Container_Boundary(api, \"API\") {\r\n    Component( ctrl , \"Controller\" , \"ASP.NET Core\", \"Accepts bookings, updates cache\", $tags = \"web\") ' Keep options\r\n    ComponentDb(data, \"Storage\", \"SQL\")\r\n  }\r\n}\r\nRel_R ( user , ctrl , \"Calls\", \"HTTPS\", \"Payload, response\" ) ' Keep edge\r\nBiRel_D(ctrl, data, \"Reads\", \"SQL\")\r\n@enduml";
        var document = new StudioDocument(source);
        var controller = document.Find("ctrl")!;
        var boundary = document.Find("site")!;
        check(document.Page.Family == "C4" && controller.CanRename && controller.IsConnectable && controller.Label == "Controller", "C4 literal card is editable with its global source alias");
        check(boundary.CanRename && boundary.EndLine == 8 && document.Find("api")!.EndLine == 7, "C4 boundaries retain complete source ranges");
        check(document.Rename(controller, "Request handler") && document.Source == source.Replace("\"Controller\"", "\"Request handler\""), "C4 card rename preserves every other argument, option, comment and line ending");
        check(!document.Rename(boundary, "Stale boundary"), "C4 stale label edit is rejected");
        check(document.Undo() && document.Source == source, "C4 label undo is exact");
        boundary = document.Find("site")!;
        check(document.Rename(boundary, "Reservations") && document.Source == source.Replace("\"Booking\"", "\"Reservations\""), "C4 boundary rename preserves nested source and named literal options");
        check(document.Undo() && document.Source == source, "C4 boundary undo is exact");

        var edge = document.Objects.Single(o => o.Arrow == "Rel_R");
        check(edge.From == "user" && edge.To == "ctrl" && edge.EditableLabel == "Calls", "C4 relationship exposes lexical endpoints and its literal macro");
        check(document.Rename(edge, "Invokes") && document.Source == source.Replace("\"Calls\"", "\"Invokes\""), "C4 inline relationship label preserves direction and technology");
        edge = document.Objects.Single(o => o.Arrow == "Rel_R");
        check(document.UpdateConnection(edge, null, document.Find("data"), edge.Arrow) &&
            document.Source == source.Replace("user , ctrl , \"Calls\"", "user , data , \"Invokes\""), "C4 target reattachment changes only the target argument");
        check(document.Undo() && document.Undo() && document.Source == source, "C4 relationship changes undo separately");
        edge = document.Objects.Single(o => o.Arrow == "Rel_R");
        check(document.UpdateConnection(edge, document.Find("data"), document.Find("user"), "BiRel_L", "") &&
            document.Source == source.Replace("Rel_R ( user , ctrl , \"Calls\"", "BiRel_L ( data , user , \"\""), "C4 simultaneous direction, endpoint and empty-label edit preserves punctuation and optional arguments");
        check(document.Undo() && document.Source == source, "C4 connection update is one undo operation");
        edge = document.Objects.Single(o => o.Arrow == "BiRel_D");
        check(document.UpdateConnection(edge, document.Find("user"), null, edge.Arrow) &&
            document.Source == source.Replace("BiRel_D(ctrl, data", "BiRel_D(user, data"), "C4 bidirectional source reattachment preserves macro direction");
        check(document.Undo() && document.Source == source, "undo C4 bidirectional endpoint edit");
        controller = document.Find("ctrl")!;
        var customer = document.Find("user")!;
        check(document.Connect(controller, customer, label: "Response, status") && document.Source.EndsWith("Rel(ctrl, user, \"Response, status\")\r\n@enduml", StringComparison.Ordinal), "C4 connect gesture emits a literal Rel call with global aliases");
        check(!document.Connect(controller, customer), "C4 stale connection endpoints are rejected");
        check(document.Undo() && document.Source == source, "undo C4 new relationship");

        controller = document.Find("ctrl")!;
        edge = document.Objects.Single(o => o.Arrow == "Rel_R");
        foreach (string literalLabel in new[] { "line\nbreak", "quote\"value", "path\\value", "$computed", "%date()" })
        {
            check(document.Rename(document.Find("ctrl")!, literalLabel) && document.Find("ctrl")!.Label == literalLabel,
                "C4 display text escapes literal quotes, slashes, newlines and preprocessor sigils");
            check(document.Undo() && document.Source == source, "C4 escaped label undo is exact");
        }
        controller = document.Find("ctrl")!;
        edge = document.Objects.Single(o => o.Arrow == "Rel_R");
        check(!document.UpdateConnection(edge, null, null, "*--") && !document.UpdateConnection(edge, null, null, "Rel_Unknown"), "C4 rejects connectors with unsupported macro semantics");
        check(!controller.CanSetFill && !controller.CanInsertMember && !document.SetFill(controller, "#123456") && !document.InsertMember(controller, "field"), "C4 macro metadata cannot enable ordinary UML member or color insertion");
        check(!document.Group([controller], "Group") && !document.PlaceRelative(controller, document.Find("data")!, "right") &&
            !document.MoveBefore(controller, document.Find("data")!) && !document.Move(controller, 1), "C4 generic grouping, hidden edges and statement reordering are disabled");
        check(document.Duplicate(controller) && document.Find("ctrlCopy")?.Label == "Controller" && document.Source.Contains("Component( ctrlCopy , \"Controller\" , \"ASP.NET Core\", \"Accepts bookings, updates cache\", $tags = \"web\") ' Keep options"), "C4 literal clone preserves named options and comments");
        check(document.Undo() && document.Source == source, "undo C4 literal clone with options");

        foreach (string statement in new[]
        {
            "Component(api, $label, \"C#\")", "Component($alias, \"API\", \"C#\")", "Component(api, \"$label\", \"C#\")",
            "Component(api, \"API\", \"C#\", $description)", "Component(api, \"API\", \"C#\", $tags = $tags)",
            "Rel(a, b, %upper(\"label\"))", "Rel(a, $target, \"Calls\")", "Unknown(api, \"API\")"
        })
        {
            var generated = new StudioDocument("@startuml\n!include <C4/C4_Component>\n" + statement + "\n@enduml");
            var item = generated.Find(null, 2)!;
            check(item.Kind == "Source" && !item.CanRename && !item.IsConnectable, "C4 expression remains a verbatim source statement " + statement);
        }

        const string basic = "@startuml\n!include <C4/C4_Component>\nPerson(user, \"Customer\")\nSystem_Boundary(site, \"Booking\") {\n  Container_Boundary(api, \"API\") {\n    Component(ctrl, \"Controller\", \"C#\", \"Handles bookings\")\n    ComponentDb(data, \"Storage\", \"SQL\")\n    BiRel_R(ctrl, data, \"Reads\", \"SQL\")\n  }\n}\nRel(user, ctrl, \"Calls\", \"HTTPS\")\n@enduml";
        var clone = new StudioDocument(basic);
        string fragment = clone.CopySelection([clone.Find("site")!])!;
        check(clone.Duplicate(clone.Find("site")!) && clone.Find("siteCopy") != null && clone.Find("ctrlCopy") != null &&
            clone.Source.Contains("BiRel_R(ctrlCopy, dataCopy, \"Reads\", \"SQL\")") && clone.Source.Contains("Rel(user, ctrl, \"Calls\", \"HTTPS\")"), "C4 duplicate reuses alias remapping for a literal nested boundary and internal edges");
        check(clone.Undo() && clone.Source == basic, "C4 nested duplicate undo is exact");
        check(clone.PasteSelection(fragment) && clone.Find("ctrlCopy") != null && clone.Find("dataCopy") != null, "C4 fragment paste remaps nested literal aliases");
        check(clone.Undo() && clone.Source == basic, "C4 paste undo is exact");
        check(!clone.PasteSelection("System_Boundary(site, \"Booking\") {\nUNKNOWN(ctrl)\n}") &&
            !clone.PasteSelection("Component(ctrl, $label, \"C#\")"), "C4 clone and paste refuse unknown or generated macro bodies");
        check(clone.Remove(clone.Find("site")!) && clone.Find("user") != null && !clone.Source.Contains("Rel(user, ctrl"), "C4 boundary deletion removes literal incident relationships");
        check(clone.Undo() && clone.Source == basic, "C4 delete undo is exact");

        var containerTool = StudioCatalog.Tools.Single(t => t.Group == "C4 containers" && t.Name == "Container");
        var componentTool = StudioCatalog.Tools.Single(t => t.Group == "C4 components" && t.Name == "Component");
        var deploymentTool = StudioCatalog.Tools.Single(t => t.Group == "C4 deployment");
        foreach (string module in new[] { "Context", "Container", "Component", "Deployment" })
        {
            var template = new StudioDocument("@startuml\n!include <C4/C4_" + module + ">\n@enduml");
            check(StudioCatalog.Accepts(containerTool, template.Page) == (module != "Context") &&
                StudioCatalog.Accepts(componentTool, template.Page) == (module == "Component") &&
                StudioCatalog.Accepts(deploymentTool, template.Page) == (module == "Deployment"), "C4 toolbox follows pinned include level " + module);
            foreach (var tool in StudioCatalog.ForPage(template.Page))
            {
                var repeat = new StudioDocument(template.Source);
                check(repeat.Add(tool) && repeat.Add(tool) && repeat.Objects.Where(o => o.IsConnectable).Select(o => o.Alias).Distinct(StringComparer.Ordinal).Count() == 2,
                    "C4 toolbox inserts independent repeated aliases " + module + " / " + tool.Name);
            }
        }
        var commentedModule = new StudioDocument("@startuml\n/'\n!include <C4/C4_Component>\n'/\n!include   <C4/C4_Context> ' pinned context\nPerson(user, \"Customer\")\n@enduml");
        check(commentedModule.Page.Family == "C4" && !StudioCatalog.Accepts(containerTool, commentedModule.Page) && !commentedModule.Add(componentTool), "C4 toolbox ignores commented includes and accepts directive whitespace");
    }
}
