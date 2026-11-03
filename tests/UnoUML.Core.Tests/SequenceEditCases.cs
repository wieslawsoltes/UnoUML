using UnoUML.Core;

internal static class SequenceEditCases
{
    internal static void Run(Action<bool, string> check)
    {
        const string original = "@startuml\r\nparticipant Caller\r\nparticipant Other\r\n  create actor \"Worker service\" as Worker #AABBCC   ' create here\r\n  Caller -> Worker ++ #112233 : Spawn   ' call\r\n  return #red  Ready   ' reply\r\n  ref#LightBlue over Caller, Worker : Reuse   ' reference\r\n@enduml";
        var document = new StudioDocument(original);
        var worker = document.Find("Worker")!;
        check(document.Page.Family == "Sequence" && worker.Kind == "actor" && worker.CanRename && worker.IsConnectable, "created actor is a source-backed editable participant");
        check(document.Rename(worker, "Background worker") && document.Source == original.Replace("Worker service", "Background worker"), "created participant rename preserves create, kind, alias, color, whitespace, comments and CRLF");
        check(!document.Rename(worker, "Stale"), "created participant rejects stale spans");
        check(document.Undo() && document.Source == original, "creation rename undoes in one transaction");
        check(document.SetFill(document.Find("Worker")!, "#DDEEFF") && document.Source == original.Replace("#AABBCC", "#DDEEFF"), "created participant fill preserves declaration syntax");
        check(document.Undo() && document.Source == original, "undo creation fill");
        var call = document.Find(null, 4)!;
        check(call.Kind == "Relationship" && call.From == "Caller" && call.To == "Worker" && call.EditableLabel == "Spawn", "activation message exposes exact endpoint and label spans");
        check(document.UpdateConnection(call, document.Find("Other"), null, "->>", "Start") && document.Source == original.Replace("Caller -> Worker ++ #112233 : Spawn", "Other ->> Worker ++ #112233 : Start"), "activation message endpoint, arrow and label edits retain lifecycle suffix and color");
        check(document.Undo() && document.Source == original, "undo compound lifecycle message edit");
        check(document.Rename(document.Find(null, 5)!, "Complete") && document.Source == original.Replace("return #red  Ready", "return #red  Complete"), "return label keeps its color and spacing");
        check(document.Undo() && document.Source == original, "undo return label edit");
        check(document.Rename(document.Find(null, 6)!, "Review") && document.Source == original.Replace(": Reuse", ": Review"), "reference label keeps participant list and header color");
        check(document.Undo() && document.Source == original, "undo reference label edit");

        foreach (string kind in new[] { "", "participant ", "actor ", "control ", "database ", "queue " })
        {
            string source = "@startuml\ncreate " + kind + "Worker\nCaller -> Worker: start\n@enduml";
            var created = new StudioDocument(source);
            check(created.Find("Worker") is { CanRename: true } && created.Rename(created.Find("Worker")!, "Long worker") &&
                created.Source == source.Replace("create " + kind + "Worker", "create " + kind + "\"Long worker\" as Worker"), "unaliased created " + kind + "gets a display label without changing references");
        }

        foreach (string suffix in new[] { "++", "--", "**", "!!", "--++ #00FF00", "++-- #99AAFF", "#112233" })
        {
            string source = "@startuml\nparticipant A\nparticipant B\nparticipant C\n  A -> B " + suffix + "   ' suffix\n@enduml";
            var message = new StudioDocument(source);
            var edge = message.Find(null, 4)!;
            check(edge.Kind == "Relationship" && edge.EditableLabel == "", "unlabeled lifecycle message " + suffix);
            if (suffix == "**")
            {
                check(!message.UpdateConnection(edge, null, message.Find("C"), edge.Arrow, "Caption") && message.Source == source, "creation shortcut rejects changing the created participant");
                check(message.UpdateConnection(edge, null, null, edge.Arrow, "Caption") && message.Source == source.Replace("B **", "B ** : Caption"), "creation shortcut label preserves creation semantics");
            }
            else
                check(message.UpdateConnection(edge, null, message.Find("C"), edge.Arrow, "Caption") && message.Source == source.Replace("B " + suffix, "C " + suffix + " : Caption"), "endpoint and empty label preserve " + suffix);
            check(message.Undo() && message.Source == source, "lifecycle suffix undo " + suffix);
        }

        const string reverse = "@startuml\nparticipant A\nparticipant B\nparticipant C\n  B <- A --++ #00FF00 : Transfer ' same\n@enduml";
        var reversed = new StudioDocument(reverse);
        check(!reversed.Retarget(reversed.Find(null, 4)!, reversed.Find("C")!, reversed.Find("B")!) && reversed.Source == reverse, "deactivation retarget cannot change the participant owning the existing activation");
        check(reversed.UpdateConnection(reversed.Find(null, 4)!, reversed.Find("C"), null, "<-") && reversed.Source == reverse.Replace("B <- A", "C <- A"), "reverse activation arrow retarget preserves lexical endpoint roles and its deactivating sender");

        foreach (string source in new[] { "@startuml\nA -> B ++: enter\n@enduml", "@startuml\nA -[#red]>> B ++ #blue: enter\n@enduml", "@startuml\nref#blue over A: check\n@enduml" })
            check(new StudioDocument(source).Page.Family == "Sequence", "sequence family detection covers lifecycle syntax without declarations");

        const string noLabels = "@startuml\nA -> B: call\nreturn   ' later\nref over A, B:   ' empty reference\n@enduml";
        var labels = new StudioDocument(noLabels);
        var returned = labels.Find(null, 2)!;
        check(returned.Kind == "Return" && returned.CanRename && returned.EditableLabel == "" && !returned.IsConnectable, "unlabeled return exposes text editing only");
        check(labels.Rename(returned, "Done") && labels.Source == noLabels.Replace("return   ", "return Done   "), "unlabeled return inserts a separator and preserves trailing trivia");
        check(labels.Undo(), "undo unlabeled return");
        check(labels.Rename(labels.Find(null, 3)!, "Shared work") && labels.Source == noLabels.Replace("A, B:", "A, B:Shared work"), "empty one-line reference retains its existing colon");

        foreach (string close in new[] { "end ref", "endref", "end" })
        {
            string source = "@startuml\nparticipant A\nref#blue over \"Database: primary\", A\nclass ThisIsReferenceText\n" + close + "\nA -> A: after\n@enduml";
            var opaque = new StudioDocument(source);
            var reference = opaque.Find(null, 2)!;
            check(reference.Kind == "Source" && reference.EndLine == 4 && !reference.CanRename && opaque.Find("ThisIsReferenceText") == null && opaque.Find(null, 5)?.Kind == "Relationship", "multiline reference is opaque with " + close);
            check(!opaque.Rename(reference, "Unsafe") && opaque.Source == source, "multiline reference label remains a whole-source edit");
        }
        var aliasReference = new StudioDocument("@startuml\nparticipant A\n@enduml");
        check(!aliasReference.PasteSelection("participant A\nref over A: shared"), "clipboard declines alias remapping through an unparsed reference participant list");
        foreach (var tool in StudioCatalog.Tools.Where(t => t.Name is "Reference" or "Activated call" or "Instance lifecycle"))
        {
            var sample = new StudioDocument("@startuml\nparticipant A\nA -> A: start\n@enduml");
            string before = sample.Source;
            check(sample.Add(tool) && sample.Undo() && sample.Source == before, "sequence lifecycle toolbox inserts and undoes " + tool.Name);
        }

        const string protectedSource = "@startuml\nparticipant Caller\nparticipant Existing\nCaller -> Existing: earlier\ncreate participant Worker\nCaller -> Worker ++: create\nreturn ready\nCaller -> Worker !!: release\n@enduml";
        var protectedDocument = new StudioDocument(protectedSource);
        var createdParticipant = protectedDocument.Find("Worker")!;
        var creationCall = protectedDocument.Find(null, 5)!;
        check(!protectedDocument.Duplicate(createdParticipant) && !protectedDocument.DuplicateMany([createdParticipant, creationCall]), "creation declarations and receiving messages cannot be duplicated independently");
        check(!protectedDocument.MoveBefore(createdParticipant, protectedDocument.Find("Caller")!) && !protectedDocument.MoveBefore(creationCall, protectedDocument.Find("Existing")!), "creation declarations and receiving messages retain their source order");
        check(!protectedDocument.MoveBefore(protectedDocument.Find(null, 3)!, creationCall), "another message cannot be moved between create and its receiver");
        check(!protectedDocument.Remove(createdParticipant) && !protectedDocument.RemoveMany([creationCall]) && !protectedDocument.Remove(protectedDocument.Find("Caller")!), "removal rejects severing a creation or deleting its required incident call");
        check(!protectedDocument.UpdateConnection(creationCall, null, protectedDocument.Find("Existing"), creationCall.Arrow) && !protectedDocument.SetArrow(creationCall, "<-"), "creation receiver and direction cannot be retargeted independently");
        check(!protectedDocument.Connect(protectedDocument.Find("Caller")!, createdParticipant) && !protectedDocument.Connect(createdParticipant, protectedDocument.Find("Existing")!), "new connections cannot use a destroyed participant");
        check(protectedDocument.CopySelection([createdParticipant]) == null && !protectedDocument.PasteSelection("create participant Fresh\nCaller -> Fresh: create"), "clipboard declines unsupported creation remapping");
        check(protectedDocument.Source == protectedSource && !protectedDocument.CanUndo, "rejected lifecycle operations preserve source and history");
        check(protectedDocument.Rename(createdParticipant, "Background worker") && protectedDocument.Source.Contains("create participant \"Background worker\" as Worker"), "protected creation remains directly label-editable");

        const string explicitDestroy = "@startuml\nparticipant A\nparticipant B\nA -> B: call\ndestroy B\n@enduml";
        var terminated = new StudioDocument(explicitDestroy);
        check(!terminated.Connect(terminated.Find("A")!, terminated.Find("B")!) && terminated.Source == explicitDestroy, "explicit destroy guards appended connections");
        const string shortcutCreation = "@startuml\nparticipant A\nparticipant B\nparticipant Old\nA -> Old: used\nA -> B **: create\n@enduml";
        var shortcut = new StudioDocument(shortcutCreation);
        check(!shortcut.UpdateConnection(shortcut.Find(null, 5)!, null, shortcut.Find("Old"), "->") && shortcut.Source == shortcutCreation, "creation shortcut cannot be retargeted to an already-used lane");
        check(shortcut.Rename(shortcut.Find(null, 5)!, "Spawn") && shortcut.Source == shortcutCreation.Replace(": create", ": Spawn"), "creation shortcut keeps label editing available");
    }
}
