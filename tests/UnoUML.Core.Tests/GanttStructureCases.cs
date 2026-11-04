using UnoUML.Core;

internal static class GanttStructureCases
{
    internal static int Run()
    {
        int assertions = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Gantt authoring: " + message); assertions++; }
        void Atomic(StudioDocument doc, string before, string message)
        {
            string after = doc.Source;
            Check(doc.Undo() && doc.Source == before, message + " exact undo");
            Check(doc.Redo() && doc.Source == after, message + " exact redo");
        }
        foreach (string nl in new[] { "\n", "\r\n", "\r" })
        {
            string prefix = "\uFEFF' preface" + nl + "@startuml" + nl + "class Unrelated" + nl + "@enduml" + nl;
            string source = ("@startgantt delivery\nProject starts 2026-01-05\n' keep before task\n[Design] as [D] lasts 3 days ' keep duration\n[Build] starts at [D]'s end and lasts 5 days\n[Ship] happens at [Build]'s end\n@endgantt").ReplaceLineEndings(nl);
            var doc = new StudioDocument(prefix + source) { ActivePage = 1 };
            var chart = doc.OpenGanttDiagram()!;
            Check(chart is { Tasks.Count: 3, ProjectStart: not null }, "calendar source snapshot");
            Check(chart.Tasks[1].StartDay == 3 && chart.Tasks[1].EndDay == 7 && chart.Tasks[2].StartDay == 7, "inclusive task and milestone dependencies");
            Check(doc.RenameGanttTask(chart.Tasks[0], "Concept"), "rename aliased caption");
            Check(doc.Source == prefix + source.Replace("[Design]", "[Concept]", StringComparison.Ordinal), "alias references and all trivia retained");
            Check(!doc.RenameGanttTask(chart.Tasks[0], "Stale"), "stale task handle rejected"); Atomic(doc, prefix + source, "rename caption");
            doc.Undo(); chart = doc.OpenGanttDiagram()!;
            Check(doc.MoveGanttTask(chart.Tasks[1], 5, 7), "move and resize task");
            var moved = doc.OpenGanttDiagram()!;
            Check(moved.Tasks[1].StartDay == 5 && moved.Tasks[1].Duration == 7 && moved.Tasks[1].PredecessorId == null && moved.Tasks[2].StartDay == 11, "manual schedule replaces dependency and propagates downstream");
            Check(doc.Source.StartsWith(prefix, StringComparison.Ordinal) && doc.Source.Contains("[Design] as [D] lasts 3 days ' keep duration" + nl, StringComparison.Ordinal), "unrelated page and task literal retained");
            Atomic(doc, prefix + source, "timeline move");
            doc.Undo(); chart = doc.OpenGanttDiagram()!;
            Check(doc.ResizeGanttTask(chart.Tasks[1], 7) && doc.OpenGanttDiagram()!.Tasks[1] is { PredecessorId: "D", StartDay: 3, Duration: 7 }, "finish resize retains start dependency");
            Atomic(doc, prefix + source, "finish resize");
            doc.Undo(); chart = doc.OpenGanttDiagram()!;
            Check(!doc.SetGanttTaskSchedule(chart.Tasks[0], new(0, 3, PredecessorId: "Build")) && doc.Source == prefix + source, "dependency cycle rejected atomically");
            Check(!doc.RemoveGanttTask(chart.Tasks[0]), "dependent deletion requires explicit detachment");
            Check(doc.RemoveGanttTask(chart.Tasks[0], true), "detach direct dependent on deletion");
            var removed = doc.OpenGanttDiagram()!;
            Check(removed.Tasks.Count == 2 && removed.Tasks[0].StartDay == 3 && removed.Tasks[0].PredecessorId == null && removed.Tasks[1].StartDay == 7, "dependent dates and downstream graph preserved");
            Check(doc.Source.Contains("' keep duration", StringComparison.Ordinal) && doc.Source.Contains("' keep before task", StringComparison.Ordinal), "deletion retains comments");
            Atomic(doc, prefix + source, "remove task");
            doc.Undo(); chart = doc.OpenGanttDiagram()!;
            Check(doc.ReorderGanttTask(chart.Tasks[2], 0), "move milestone before predecessor declaration");
            var reordered = doc.OpenGanttDiagram()!;
            Check(reordered.Tasks.Select(t => t.Id).SequenceEqual(["Ship", "D", "Build"]) && reordered.Tasks[0].StartDay == 7, "reorder retains dependencies through forward reference");
            Check(doc.Source.Contains("' keep duration", StringComparison.Ordinal) && doc.Source.Contains("' keep before task", StringComparison.Ordinal), "reorder keeps comments");
            Atomic(doc, prefix + source, "reorder");
            doc.Undo(); chart = doc.OpenGanttDiagram()!;
            Check(doc.InsertGanttTask(chart, 1, "Review", "R", new(0, 2, PredecessorId: "D", Completion: 40, Color: "#123456")), "insert task at requested row");
            var inserted = doc.OpenGanttDiagram()!;
            Check(inserted.Tasks.Select(t => t.Id).SequenceEqual(["D", "R", "Build", "Ship"]) && inserted.Tasks[1].StartDay == 3 && inserted.Tasks[1].Completion == 40, "inserted task identity, order and schedule");
            Atomic(doc, prefix + source, "insert task");
            doc.Undo(); chart = doc.OpenGanttDiagram()!;
            doc.ActivePage = 0;
            Check(!doc.MoveGanttTask(chart.Tasks[0], 2, 4), "cross-page snapshot rejected");
        }

        var chain = new StudioDocument("@startgantt\n[A] lasts 2 days then [B] lasts 3 days then [C] lasts 2 days ' chain comment\n@endgantt");
        string chainSource = chain.Source;
        Check(chain.MoveGanttTask(chain.OpenGanttDiagram()!.Tasks[1], 8, 4), "edit a shared chained statement");
        var chainModel = chain.OpenGanttDiagram()!;
        Check(chainModel.Tasks[0].StartDay == 0 && chainModel.Tasks[1].StartDay == 8 && chainModel.Tasks[2].StartDay == 12, "normalization retains next chain dependency");
        Check(chain.Source.Split("chain comment", StringSplitOptions.None).Length == 2, "shared row comment retained exactly once");
        Atomic(chain, chainSource, "shared chain");
        chain.Undo(); Check(chain.RemoveGanttTask(chain.OpenGanttDiagram()!.Tasks[1], true), "remove middle chained task");
        Check(chain.OpenGanttDiagram()!.Tasks.Select(t => t.Id).SequenceEqual(["A", "C"]) && chain.OpenGanttDiagram()!.Tasks[1].StartDay == 5, "chain deletion keeps dependent date");
        Atomic(chain, chainSource, "chain deletion");

        foreach (string body in new[] { "[A] -> [B]\n[B] lasts 3 days", "[A] lasts 2 days\n[A] -> [B]", "[A] -> [B]", "[A]->[B]" })
        {
            var doc = new StudioDocument("@startgantt\n" + body + "\n@endgantt"); string before = doc.Source;
            var b = doc.OpenGanttDiagram()!.Tasks.Single(t => t.Id == "B");
            Check(doc.MoveGanttTask(b, 7, 4), "arrow target can acquire explicit schedule");
            Check(doc.OpenGanttDiagram()!.Tasks.Count == 2 && doc.OpenGanttDiagram()!.Tasks.Single(t => t.Id == "B").StartDay == 7, "arrow source retained after detachment");
            Atomic(doc, before, "arrow normalization");
        }

        var names = new StudioDocument("@startgantt\n[Review and approve] lasts 2 days\n[Release then ship] starts at [Review and approve]'s end\n[Release then ship] lasts 3 days\n@endgantt");
        string namesSource = names.Source;
        Check(names.RenameGanttTask(names.OpenGanttDiagram()!.Tasks[0], "Director's review"), "conjunctions and apostrophes in task names");
        Check(names.Source == namesSource.Replace("Review and approve", "Director's review", StringComparison.Ordinal), "unaliased rename updates precise references");
        Atomic(names, namesSource, "reference rename");
        var duplicates = new StudioDocument("@startgantt\n[Same caption] as [A] lasts 2 days\n[Same caption] as [B] lasts 4 days\n[B] starts at [A]'s end\n@endgantt");
        Check(duplicates.OpenGanttDiagram() is { Tasks.Count: 2 } duplicatesModel && duplicatesModel.Tasks[1].StartDay == 2, "duplicate captions have independent explicit aliases");
        Check(duplicates.RenameGanttTask(duplicates.OpenGanttDiagram()!.Tasks[1], "Second caption") && duplicates.Source.Contains("[B] starts at [A]'s end", StringComparison.Ordinal), "duplicate caption rename preserves aliases");
        var selfAlias = new StudioDocument("@startgantt\n[A] as [A] lasts 2 days\n[A] is 50% completed\n[B] starts at [A]'s end\n@endgantt");
        Check(selfAlias.OpenGanttDiagram()!.Tasks[0].HasAlias && selfAlias.RenameGanttTask(selfAlias.OpenGanttDiagram()!.Tasks[0], "First") &&
            selfAlias.Source.Contains("[First] as [A]", StringComparison.Ordinal) && selfAlias.Source.Contains("[A] is 50%", StringComparison.Ordinal) &&
            selfAlias.OpenGanttDiagram()!.Tasks[1].PredecessorId == "A", "explicit same-name alias retains identity on rename");
        var captionReference = new StudioDocument("@startgantt\n[Build] lasts 1 day\n[Design] as [D] lasts 3 days\n[Build] -> [Design]\n[Review] starts at [Design]'s end\n@endgantt");
        Check(captionReference.RenameGanttTask(captionReference.OpenGanttDiagram()!.Tasks.Single(t => t.Id == "D"), "Plan") &&
            captionReference.OpenGanttDiagram()!.Tasks.Count == 3 && captionReference.Source.Contains("[Build] -> [D]", StringComparison.Ordinal) &&
            captionReference.Source.Contains("[Review] starts at [D]'s end", StringComparison.Ordinal), "caption references to aliased task cannot create phantom task");
        var collision = new StudioDocument("@startgantt\n[Other] lasts 1 day\n[Design] as [D] lasts 3 days\n[Build] starts at [Design]'s end and lasts 1 day\n@endgantt");
        Check(collision.RenameGanttTask(collision.OpenGanttDiagram()!.Tasks.Single(t => t.Id == "D"), "Other") &&
            collision.OpenGanttDiagram()!.Tasks.Single(t => t.Id == "Build") is { PredecessorId: "D", StartDay: 3 }, "caption collision cannot rebind dependency away from stable alias");
        var shadow = new StudioDocument("@startgantt\n[Design] as [D] lasts 3 days\n[Build] starts at [Design]'s end and lasts 1 day\n@endgantt");
        string shadowSource = shadow.Source;
        Check(!shadow.InsertGanttTask(shadow.OpenGanttDiagram()!, 2, "Other", "Design", new(0)) && shadow.Source == shadowSource, "new alias cannot shadow an existing caption reference");
        var continuation = new StudioDocument("@startgantt\n[A] lasts 1 day\n[B] lasts 5 days\n[A] is 50% completed\nthen [C] lasts 1 day\n@endgantt");
        Check(continuation.MoveGanttTask(continuation.OpenGanttDiagram()!.Tasks[0], 2, 1) &&
            continuation.OpenGanttDiagram()!.Tasks.Single(t => t.Id == "C") is { PredecessorId: "A", StartDay: 3 }, "standalone then retains its original predecessor after contribution removal");

        var direct = new StudioDocument("@startgantt\n[Design 🧩] lasts 3 days\n[Build] starts at [Design 🧩]'s end\n@endgantt");
        int offset = direct.Source.IndexOf("Design 🧩", StringComparison.Ordinal);
        var field = direct.OpenSourceField(offset, "Design 🧩".Length, "gantt-label")!;
        Check(field != null && direct.ApplySourceField(field, "Plan 🧩") && direct.Source.Split("Plan 🧩", StringSplitOptions.None).Length == 3, "inline UTF-16 name editing updates all references");
        Check(!direct.ApplySourceField(field!, "Stale"), "inline stale source guarded");
        var wrong = new StudioDocument("@startuml\nclass Gantt\n@enduml");
        Check(wrong.OpenSourceField(wrong.Source.IndexOf("Gantt", StringComparison.Ordinal), 5, "gantt-label") == null, "family isolation");

        var calendar = new StudioDocument("@startgantt\nProject starts 2024-02-28\n[A] starts D+1 and ends D+3\n[B] happens D+4\n@endgantt");
        Check(calendar.OpenGanttDiagram()!.Tasks[0].Duration == 3 && calendar.OpenGanttDiagram()!.Tasks[1].StartDay == 4, "relative-day dates across leap boundary");
        string calendarSource = calendar.Source;
        Check(calendar.SetGanttSettings(calendar.OpenGanttDiagram()!, new(2025, 3, 1), "Release", false), "project settings transaction");
        Check(calendar.OpenGanttDiagram() is { Title: "Release", ShowFooter: false } adjusted && adjusted.Tasks[0].StartDay == 1, "relative-day constraints follow epoch change");
        Atomic(calendar, calendarSource, "settings");
        var trivia = new StudioDocument("@startgantt\nProject starts /' keep epoch '/ 2024-02-28\ntitle One /' keep title '/ Two\n[A] lasts 2 days\n@endgantt");
        string triviaSource = trivia.Source;
        Check(trivia.SetGanttSettings(trivia.OpenGanttDiagram()!, new(2025, 1, 1), "Release", true) &&
            trivia.Source.Contains("/' keep epoch '/", StringComparison.Ordinal) && trivia.Source.Contains("/' keep title '/", StringComparison.Ordinal), "settings retain embedded block comments");
        Atomic(trivia, triviaSource, "settings trivia");
        var bracketed = new StudioDocument("@startgantt\n[Plan /'comment-like'/ stage] lasts 2 days\n@endgantt");
        Check(bracketed.OpenGanttDiagram() == null, "preprocessed caption has no unsafe literal authoring projection");
        var dpi = new StudioDocument("@startgantt\nskinparam dpi 192\n[A] lasts 2 days\n@endgantt");
        Check(dpi.MoveGanttTask(dpi.OpenGanttDiagram()!.Tasks[0], 1, 2) && dpi.Source.Contains("skinparam dpi 192", StringComparison.Ordinal), "DPI directive remains unchanged during semantic gesture");

        var empty = new StudioDocument("@startgantt\n' empty\n@endgantt");
        Check(empty.InsertGanttTask(empty.OpenGanttDiagram()!, 0, "First", "A", new(1, 1, true)), "author an empty page");
        Check(empty.RemoveGanttTask(empty.OpenGanttDiagram()!.Tasks[0]) && empty.OpenGanttDiagram()!.Tasks.Count == 0, "remove final task leaves editable page");
        foreach (var invalid in new[] { new GanttTaskSchedule(-1), new(0, 0), new(1095, 2), new(0, 2, true), new(0, 1, true, Completion: 20), new(0, Completion: 101), new(0, Color: "red"), new(0, PredecessorId: "Missing") })
        {
            string before = empty.Source; long state = empty.HistoryState;
            Check(!empty.InsertGanttTask(empty.OpenGanttDiagram()!, 0, "Invalid", "", invalid) && empty.Source == before && empty.HistoryState == state, "invalid schedule does not mutate source/history");
        }
        foreach (string invalidName in new[] { "A]\n[B", "[Nested]", "A\nB", "", " Leading" })
            Check(!empty.InsertGanttTask(empty.OpenGanttDiagram()!, 0, invalidName, "", new(0)), "name injection rejected");
        foreach (string unsupported in new[] { "[A] starts at [Missing]'s end", "[A] lasts 2 days\nsunday are closed", "[A] lasts 2 days\n[A] starts at [B]'s end\n[B] starts at [A]'s end", "[A] happens D+0 and is 20% completed" })
            Check(new StudioDocument("@startgantt\n" + unsupported + "\n@endgantt").OpenGanttDiagram() == null, "unsupported or invalid page is not partially edited");
        return assertions;
    }
}
