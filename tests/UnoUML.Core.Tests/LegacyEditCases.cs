using UnoUML.Core;

internal static class LegacyEditCases
{
    internal static void Run(Action<bool, string> check)
    {
        ChenEdits(check);
        DitaaEdits(check);
        foreach (var tool in StudioCatalog.Tools.Where(t => t.Families is "Chen" or "Ditaa"))
        {
            var document = new StudioDocument(tool.Families == "Chen" ? "@startchen\n@endchen" : "@startditaa\n@endditaa");
            check(document.Add(tool) && document.Add(tool), "repeatable dialect toolbox item " + tool.Name);
        }
    }

    private static void ChenEdits(Action<bool, string> check)
    {
        const string source = "@startchen\r\n' Keep original comment\r\nentity ACCOUNT {\r\n  Id : INTEGER <<key>>\r\n}\r\nentity OTHER {\r\n}\r\nrelationship OWNS {\r\n}\r\nOWNS  -(0,N)-   ACCOUNT\r\n@endchen";
        var document = new StudioDocument(source);
        var account = document.Find("ACCOUNT")!;
        var other = document.Find("OTHER")!;
        var relation = document.Find("OWNS")!;
        check(account.IsConnectable && account.CanInsertMember && relation.IsConnectable, "Chen declarations in inspector");
        check(document.Rename(account, "Bank account") && document.Source.Contains("entity \"Bank account\" as ACCOUNT {\r\n"), "Chen rename preserves stable identity and CRLF");
        check(document.Undo() && document.Source == source, "one Chen rename undo");
        var field = document.Find("ACCOUNT/Id : INTEGER")!;
        check(!field.IsConnectable && document.Rename(field, "Account number") && document.Source.Contains("\"Account number\" as Id : INTEGER <<key>>"), "Chen attribute rename preserves type and key");
        check(document.Undo() && document.Source == source, "Chen attribute undo");

        account = document.Find("ACCOUNT")!;
        other = document.Find("OTHER")!;
        relation = document.Find("OWNS")!;
        check(document.Connect(relation, account) && document.Source.Contains("OWNS -1- ACCOUNT"), "Chen default connect gesture");
        check(document.Undo(), "undo Chen link");
        account = document.Find("ACCOUNT")!;
        other = document.Find("OTHER")!;
        check(document.Connect(account, other, "-N=") && document.Source.Contains("ACCOUNT -N= OTHER"), "Chen association accepts upstream endpoint types and mixed participation strokes");
        check(document.Undo(), "undo Chen entity association");

        var edge = document.Objects.Single(o => o.Kind == "Relationship");
        check(document.Rename(edge, "(1,N)") && document.Source.Contains("OWNS  -(1,N)-   ACCOUNT"), "Chen edit cardinality without rebuilding statement");
        edge = document.Objects.Single(o => o.Kind == "Relationship");
        other = document.Find("OTHER")!;
        check(document.UpdateConnection(edge, null, other, "=N=") && document.Source.Contains("OWNS  =N=   OTHER"), "Chen retarget total participation");
        edge = document.Objects.Single(o => o.Kind == "Relationship");
        check(!document.Rename(edge, "a bad cardinality") && !document.UpdateConnection(edge, null, null, "*--") &&
            !document.UpdateConnection(edge, null, null, "-N-\n"), "Chen refuses invalid arrow, cardinality and trailing line break");
        check(document.Rename(edge, "") && document.Source.Contains("OWNS  ==   OTHER"), "Chen cardinality may be cleared through inspector rename");
        edge = document.Objects.Single(o => o.Kind == "Relationship");
        check(document.UpdateConnection(edge, null, null, "-N=", "(0,1)") && document.Source.Contains("OWNS  -(0,1)=   OTHER"), "Chen cardinality preserves both participation strokes");

        account = document.Find("ACCOUNT")!;
        check(!document.InsertMember(account, "Id : INTEGER <<key>>") && document.InsertMember(account, "Balance : REAL <<derived>>") &&
            document.Find("ACCOUNT/Balance : REAL") != null, "Chen attribute insertion and collision check");
        account = document.Find("ACCOUNT")!;
        check(document.SetFill(account, "#abcdef") && document.Source.Contains("entity ACCOUNT #abcdef {\r\n"), "Chen fill insertion preserves declaration syntax");
        account = document.Find("ACCOUNT")!;
        check(document.SetFill(account, "#123456") && document.Source.Contains("entity ACCOUNT #123456 {\r\n"), "Chen fill replacement preserves source");

        var nested = new StudioDocument("@startchen\nentity A {\n  Address {\n    City\n  }\n}\n@endchen");
        var address = nested.Find("A/Address")!;
        check(address.CanInsertMember && nested.InsertMember(address, "Country") && nested.Find("A/Address/Country") != null, "Chen inserts nested composite attributes");
        address = nested.Find("A/Address")!;
        check(!nested.InsertMember(address, "Country") && !nested.InsertMember(address, "Unclosed {"), "nested Chen insertion rejects duplicate and incomplete attributes");
        check(nested.Rename(address, "Postal address") && nested.Find("A/Address/City") != null, "composite attribute rename keeps child identities");

        var clone = new StudioDocument(source);
        string fragment = clone.CopySelection([clone.Find("ACCOUNT")!, clone.Find("OWNS")!])!;
        check(fragment.Contains("OWNS  -(0,N)-   ACCOUNT") && !fragment.Contains("entity OTHER"), "Chen copy includes only induced associations");
        check(clone.DuplicateMany([clone.Find("ACCOUNT")!, clone.Find("OWNS")!]) &&
            clone.Find("ACCOUNTCopy/Id : INTEGER") != null && clone.Find("OWNSCopy") != null &&
            clone.Source.Contains("OWNSCopy  -(0,N)-   ACCOUNTCopy"), "Chen duplicate remaps declarations and cardinality endpoints together");
        check(clone.Undo() && clone.Source == source, "Chen duplicate undo is atomic");
        check(clone.Duplicate(clone.Find("ACCOUNT")!) && clone.Find("ACCOUNTCopy/Id : INTEGER") != null, "Chen inspector duplicate includes nested attributes");
        check(clone.Undo() && clone.Source == source, "undo Chen inspector duplicate");
        check(clone.PasteSelection(fragment) && clone.Find("ACCOUNTCopy") != null && clone.Source.Contains("OWNSCopy  -(0,N)-   ACCOUNTCopy"), "Chen paste uses its own dialect for alias remapping");
        check(clone.Undo() && clone.Source == source, "undo Chen paste");
        check(!clone.Duplicate(clone.Find("ACCOUNT/Id : INTEGER")!) && !clone.PasteSelection("Id : INTEGER <<key>>"), "Chen refuses standalone attribute duplication or top-level paste");
        check(clone.Remove(clone.Find("ACCOUNT")!) && !clone.Source.Contains("OWNS  -(0,N)-") && clone.Find("OWNS") != null, "Chen deletion removes incident links and preserves relationship nodes");
        check(clone.Undo() && clone.Source == source, "Chen deletion undo restores exact source");
    }

    private static void DitaaEdits(Action<bool, string> check)
    {
        const string source = "@startuml\nA -> B\n@enduml\n\n@startditaa\n+--------+     +-------+\n| Client +---->| Store |\n+--------+     +-------+\n@endditaa";
        var document = new StudioDocument(source) { ActivePage = 1 };
        var first = document.Find("ditaa:2:2", 6)!;
        var second = document.Find("ditaa:2:17", 6)!;
        check(first.Label == "Client" && second.Label == "Store" && !first.IsConnectable, "independent Ditaa selections on the same line");
        check(document.Find(null, 6) == null && document.Find("ditaa:2:2", 3) == null, "unmapped Ditaa hit never guesses a text run");
        check(document.FindAtOffset(source.IndexOf("Store", StringComparison.Ordinal), out int page) == second && page == 1, "caret maps to correct same-row Ditaa run");
        check(document.Rename(first, "UI") && document.Source.Contains("| UI     +---->| Store |"), "short label retains every ASCII border and connector");
        check(!document.Rename(second, "stale"), "stale Ditaa edit is rejected");
        second = document.Find("ditaa:2:17", 6)!;
        check(!document.Rename(second, "A very long name") && document.Rename(second, "Stores") && document.Source.Contains("| Stores|"), "growth consumes only unused source cells");
        check(document.Undo() && document.Undo() && document.Source == source, "Ditaa transactional undo restores exact source");
        first = document.Find("ditaa:2:2", 6)!;
        second = document.Find("ditaa:2:17", 6)!;
        check(!document.Rename(first, "cRED") && !document.Rename(first, "|") && !document.Duplicate(first), "Ditaa text editing cannot inject ink or control tags");
        check(document.CopySelection([second, first, second]) == "Client\nStore", "Ditaa copy retains every distinct same-row label in source order");
        check(document.CutSelection([first, second], out string cut) && cut == "Client\nStore" && document.Source.Contains("|        +---->|       |"), "Ditaa cut copies all labels and preserves the grid");
        check(document.Undo() && document.Source == source, "Ditaa cut undo restores exact source");
        first = document.Find("ditaa:2:2", 6)!;
        second = document.Find("ditaa:2:17", 6)!;
        check(document.RemoveMany([first, second]) && document.Source.Contains("|        +---->|       |") && document.Undo() && document.Source == source, "multi-label deletion keeps the entire grid intact");
        first = document.Find("ditaa:2:2", 6)!;
        check(!document.Replace(first, "+bad+") && document.Replace(first, "UI") && document.Source.Contains("| UI     +---->"), "Ditaa statement dialog obeys the same cell-safe label edit");
        check(document.Undo() && document.Source == source, "undo Ditaa statement edit");

        const string taggedSource = "@startditaa\r\n+-----------------------+\r\n| A  B   cRED  {d}      |\r\n+-----------------------+\r\n@endditaa";
        var tagged = new StudioDocument(taggedSource);
        var a = tagged.Find("ditaa:2:2")!;
        var b = tagged.Find("ditaa:2:5")!;
        check(tagged.Objects.Count(o => o.Kind == "ASCII label") == 2, "Ditaa control tags are excluded from editable labels");
        check(!tagged.Rename(a, "Long") && tagged.Rename(b, "Be") && tagged.Source.Contains("| A  Be  cRED  {d}      |"), "Ditaa label growth preserves neighboring runs and tags");
        check(tagged.Undo() && tagged.Source == taggedSource, "Ditaa label changes preserve CRLF");

        var arrow = new StudioDocument("@startditaa\n+-------+\n|  |    |\n|  A    |\n+-------+\n@endditaa");
        check(!arrow.Rename(arrow.Find("ditaa:3:3")!, "V"), "Ditaa label edit cannot join an existing vertical wire");
    }
}
