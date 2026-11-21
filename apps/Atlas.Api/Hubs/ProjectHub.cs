using System.Collections.Concurrent;
using Atlas.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TaskCollab.Infrastructure;

namespace Atlas.Api.Hubs;

[Authorize(Policy = "AtlasRead")]
public sealed class ProjectHub(ProjectHubConnections connections, PostgresTaskCollabStore store) : Hub
{
    public async Task Subscribe(Guid projectId, CancellationToken cancellationToken)
    {
        var principal = await ResolvePrincipal(cancellationToken);
        if (!await store.CanReadProjectAsync(principal.PrincipalId, projectId, cancellationToken)) throw new HubException("Project subscription is not available.");
        if (connections.Remove(Context.ConnectionId, out var previousProject, out var previousPrincipal))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, connections.Group(previousProject, previousPrincipal), cancellationToken);
        await Groups.AddToGroupAsync(Context.ConnectionId, connections.Group(projectId, principal.PrincipalId), cancellationToken);
        connections.Add(projectId, principal.PrincipalId, Context.ConnectionId);
    }

    public async Task Unsubscribe(Guid projectId, CancellationToken cancellationToken)
    {
        if (connections.Remove(Context.ConnectionId, out var project, out var principal))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, connections.Group(project, principal), cancellationToken);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.Remove(Context.ConnectionId, out var project, out var principal))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, connections.Group(project, principal));
        await base.OnDisconnectedAsync(exception);
    }

    private Task<ProjectIdentity.Contracts.AtlasPrincipal> ResolvePrincipal(CancellationToken cancellationToken) =>
        Context.User!.ResolveAtlasPrincipalAsync(Context.GetHttpContext()!.RequestServices, cancellationToken);
}

public sealed class ProjectHubConnections
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>>> subscriptions = new();
    private readonly ConcurrentDictionary<string, (Guid Project, Guid Principal)> connections = new();

    public string Group(Guid project, Guid principal) => $"project:{project:N}:principal:{principal:N}";
    public void Add(Guid project, Guid principal, string connection)
    {
        connections[connection] = (project, principal);
        subscriptions.GetOrAdd(project, _ => new()).GetOrAdd(principal, _ => new())[connection] = 0;
    }

    public bool Remove(string connection, out Guid project, out Guid principal)
    {
        if (!connections.TryRemove(connection, out var value)) { project = default; principal = default; return false; }
        (project, principal) = value;
        if (subscriptions.TryGetValue(project, out var members) && members.TryGetValue(principal, out var ids))
        {
            ids.TryRemove(connection, out _);
            if (ids.IsEmpty) members.TryRemove(principal, out _);
        }
        if (subscriptions.TryGetValue(project, out members) && members.IsEmpty) subscriptions.TryRemove(project, out _);
        return true;
    }

    public IReadOnlyCollection<Guid> Principals(Guid project) => subscriptions.TryGetValue(project, out var members) ? members.Keys.ToArray() : [];

    public IReadOnlyCollection<string> RemovePrincipal(Guid project, Guid principal)
    {
        if (!subscriptions.TryGetValue(project, out var members) || !members.TryRemove(principal, out var ids)) return [];
        foreach (var connection in ids.Keys) connections.TryRemove(connection, out _);
        if (members.IsEmpty) subscriptions.TryRemove(project, out _);
        return ids.Keys.ToArray();
    }
}

public sealed class ProjectRealtimeNotifier(IHubContext<ProjectHub> hub, ProjectHubConnections connections, PostgresTaskCollabStore store)
{
    public async Task RevokePrincipalAsync(Guid project, Guid principal, CancellationToken cancellationToken = default)
    {
        var group = connections.Group(project, principal);
        await hub.Clients.Group(group).SendCoreAsync("authorizationChanged", [new { refresh = Guid.NewGuid() }], cancellationToken);
        foreach (var connection in connections.RemovePrincipal(project, principal))
            await hub.Groups.RemoveFromGroupAsync(connection, group, cancellationToken);
    }

    public async Task PublishAsync(Guid project, string kind, CancellationToken cancellationToken = default)
    {
        foreach (var principal in connections.Principals(project))
        {
            if (!await store.CanReadProjectAsync(principal, project, cancellationToken))
            {
                var group = connections.Group(project, principal);
                await hub.Clients.Group(group).SendCoreAsync("authorizationChanged", [new { refresh = Guid.NewGuid() }], cancellationToken);
                foreach (var connection in connections.RemovePrincipal(project, principal))
                    await hub.Groups.RemoveFromGroupAsync(connection, group, cancellationToken);
                continue;
            }
            // IDs, authors, bodies, titles, counts and source names intentionally stay out of push events.
            await hub.Clients.Group(connections.Group(project, principal)).SendCoreAsync("changed", [new { kind, refresh = Guid.NewGuid() }], cancellationToken);
        }
    }
}
