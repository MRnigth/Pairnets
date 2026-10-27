namespace Pairnets.Desktop.Platform;

/// <summary>
/// What a clicked notice does, by the notice's id: the newest few are kept (a notice can be clicked again
/// in Notification Center), older ones are forgotten.
/// </summary>
internal sealed class NoticeActions(int capacity = 50)
{
    private readonly object _gate = new();
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Action> _actions = [];

    public void Add(string id, Action? action)
    {
        if (action is null)
            return;
        lock (_gate)
        {
            _actions[id] = action;
            _order.Enqueue(id);
            while (_order.Count > capacity)
                _actions.Remove(_order.Dequeue());
        }
    }

    public Action? Get(string id)
    {
        lock (_gate)
            return _actions.GetValueOrDefault(id);
    }
}
