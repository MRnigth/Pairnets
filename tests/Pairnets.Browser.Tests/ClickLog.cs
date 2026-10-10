using System.Collections.Concurrent;

namespace Pairnets.Browser.Tests;

/// <summary>
/// What the browser really did across all the website tests: every button, link, form and checkbox that got a click,
/// submit or change, every one that appeared on a page at all, and every request to the website's API. The names
/// ("keys") come from the same small script in the browser, for the live pages and for the pages' source alike.
/// </summary>
public sealed class ClickLog
{
    /// <summary>What counts as something to press: buttons, links, forms and checkboxes.</summary>
    public const string Interactive = "button, a[href], form, input[type=checkbox]";

    /// <summary>
    /// <c>window.__pnKey(element)</c>: "devices button#add", "devices #rename-form button[data-close] "Cancel"",
    /// "devices #devices button "Rename…"". An element is named by its id, or else by the nearest ancestor with an id
    /// plus what it is and its text, so the rows the pages build at run time get one name for all their copies.
    /// </summary>
    public const string KeyScript = """
        window.__pnKey = (el) => {
          const doc = el.ownerDocument;
          const page = (doc.body && doc.body.dataset.page) || '?';
          const tag = el.tagName.toLowerCase();
          if (el.id) return page + ' ' + tag + '#' + el.id;
          const owner = el.parentElement ? el.parentElement.closest('[id]') : null;
          let what = tag;
          if (el.hasAttribute('data-close')) what += '[data-close]';
          if (el.hasAttribute('data-nav')) what += '[data-nav=' + el.getAttribute('data-nav') + ']';
          if (tag === 'a') what += '[href="' + el.getAttribute('href') + '"]';
          const text = (el.textContent || '').trim().replace(/\s+/g, ' ');
          what += text ? ' "' + text + '"' : (el.classList.length ? '.' + Array.from(el.classList).join('.') : '');
          return page + ' ' + (owner ? '#' + owner.id + ' ' : '') + what;
        };
        """;

    /// <summary>
    /// Runs in every page before its own script: reports clicks, submits and checkbox changes as they happen (capture
    /// phase, so before the page's handlers can navigate away), and every interactive element that enters the page.
    /// </summary>
    public const string RecorderScript = KeyScript + """
        (() => {
          const send = (kind, el) => { try { window.__pnRecord(kind, window.__pnKey(el)); } catch (e) { } };
          document.addEventListener('click', (e) => {
            const el = e.target && e.target.closest ? e.target.closest('button, a[href], input[type=checkbox]') : null;
            if (el) send('click', el);
          }, true);
          document.addEventListener('submit', (e) => send('submit', e.target), true);
          document.addEventListener('change', (e) => { if (e.target.matches && e.target.matches('input[type=checkbox]')) send('click', e.target); }, true);
          const seen = (node) => {
            if (node.nodeType !== 1) return;
            if (node.matches('button, a[href], form, input[type=checkbox]')) send('seen', node);
            node.querySelectorAll('button, a[href], form, input[type=checkbox]').forEach((n) => send('seen', n));
          };
          new MutationObserver((changes) => changes.forEach((c) => c.addedNodes.forEach(seen))).observe(document, { childList: true, subtree: true });
        })();
        """;

    /// <summary>Names every interactive element in the given page sources (parsed, not run).</summary>
    public const string InventoryScript = """
        (sources) => {
          const keys = [];
          for (const html of sources) {
            const doc = new DOMParser().parseFromString(html, 'text/html');
            doc.querySelectorAll('button, a[href], form, input[type=checkbox]').forEach((el) => keys.push(window.__pnKey(el)));
          }
          return keys;
        }
        """;

    private readonly ConcurrentDictionary<string, int> _used = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Method, string Path)> _requests = new();
    private readonly ConcurrentDictionary<string, int> _flows = new(StringComparer.Ordinal);

    /// <summary>Elements that got a click, a submit or a change.</summary>
    public IReadOnlySet<string> Used => _used.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Elements that were on a page at some point.</summary>
    public IReadOnlySet<string> Seen => _seen.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Requests to /web/api/… ("POST", "/devices/forget"), without the prefix.</summary>
    public IReadOnlyList<(string Method, string Path)> ApiRequests => _requests.ToList();

    /// <summary>The flows that finished without a problem.</summary>
    public IReadOnlySet<string> FlowsDone => _flows.Keys.ToHashSet(StringComparer.Ordinal);

    public void Record(string kind, string key)
    {
        if (kind == "seen")
            _seen.AddOrUpdate(key, 1, (_, n) => n + 1);
        else
            _used.AddOrUpdate(key, 1, (_, n) => n + 1);
    }

    public void Request(string method, string url)
    {
        var path = new Uri(url).AbsolutePath;
        if (path.StartsWith("/web/api/", StringComparison.Ordinal))
            _requests.Enqueue((method, path["/web/api".Length..]));
    }

    public void FlowDone(string flow) => _flows.TryAdd(flow, 0);
}
