namespace UnoUML.PlantUml;

internal static partial class SequenceRenderer
{
    // The activation stack records the call that opened each bar. Returns use
    // that call, including nested self calls, rather than the most recent arrow.
    private sealed class LifeState(Diagram diagram)
    {
        private readonly Stack<Step> calls = [];
        private Step? lastMessage;
        internal bool Automatic, AfterDelay;
        internal int PendingCreate = -1;

        internal void Barrier() => lastMessage = null;

        internal bool Create(int lane)
        {
            var participant = diagram.Lanes[lane];
            if (AfterDelay || PendingCreate >= 0 || participant.Used || participant.Creation != null || participant.ActiveDepth != 0) return false;
            PendingCreate = lane;
            return true;
        }

        internal bool Send(Step step, string activation, string? color)
        {
            if (activation == "**" && !Create(step.B)) return false;
            if (!AddMessage(step)) return false;
            if (activation.Length == 0)
            {
                if (!Automatic) return color == null;
                return step.Variant.Contains("--", StringComparison.Ordinal)
                    ? Change(Op.Finish, step.A, step.Line)
                    : Change(Op.Start, step.B, step.Line, color);
            }
            if (color != null && !activation.Contains("++", StringComparison.Ordinal)) return false;
            for (int i = 0; i < activation.Length; i += 2)
            {
                bool ok = activation[i] switch
                {
                    '+' => Change(Op.Start, step.B, step.Line, color),
                    '-' => Change(Op.Finish, step.A, step.Line),
                    '!' => Change(Op.Destroy, step.B, step.Line),
                    '*' => true,
                    _ => false
                };
                if (!ok) return false;
            }
            return true;
        }

        private bool AddMessage(Step step)
        {
            var from = diagram.Lanes[step.A];
            var to = diagram.Lanes[step.B];
            if (from.Destruction != null || to.Destruction != null) return false;
            if (PendingCreate >= 0)
            {
                if (step.B != PendingCreate || step.A == step.B) return false;
                step.Creates = true;
                to.Creation = step;
                PendingCreate = -1;
            }
            from.Used = to.Used = true;
            diagram.Steps.Add(step);
            lastMessage = step;
            AfterDelay = false;
            return true;
        }

        internal Step? Reply(int line, string text, string? color)
        {
            bool deactivates = calls.TryPeek(out var call);
            call ??= lastMessage;
            if (call == null) return null;
            string arrow = call.Variant.Contains(">>", StringComparison.Ordinal) || call.Variant.Contains("<<", StringComparison.Ordinal) ? "-->>" : "-->";
            var reply = new Step(Op.Send, line, call.B, call.A, text, arrow, color ?? call.Color);
            if (!AddMessage(reply) || deactivates && !Change(Op.Finish, call.B, line)) return null;
            return reply;
        }

        internal bool Change(Op operation, int lane, int line, string? color = null)
        {
            var participant = diagram.Lanes[lane];
            if (AfterDelay || participant.Destruction != null || PendingCreate == lane) return false;
            bool involved = lastMessage != null && (lastMessage.A == lane || lastMessage.B == lane);
            var step = new Step(operation, line, lane, color: color);
            switch (operation)
            {
                case Op.Start:
                    if (participant.ActiveDepth >= 64) return false;
                    participant.ActiveDepth++;
                    if (involved)
                    {
                        // Activating a sender is legal upstream but its return
                        // routing is ambiguous without a target activation.
                        if (lastMessage!.B != lane) return false;
                        calls.Push(lastMessage);
                    }
                    break;
                case Op.Finish:
                    if (participant.ActiveDepth == 0) return false;
                    participant.ActiveDepth--;
                    if (involved && calls.Count != 0) calls.Pop();
                    break;
                case Op.Destroy:
                    participant.Destruction = step;
                    participant.ActiveDepth = 0;
                    step.Standalone = !involved;
                    break;
            }
            diagram.Steps.Add(step);
            return true;
        }
    }
}
