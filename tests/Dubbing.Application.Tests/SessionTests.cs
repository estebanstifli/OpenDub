using Dubbing.Application.Projects;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Tests;

public sealed class SessionTests
{
    [Fact] public void PristineProjectOnlyBecomesDirtyAfterAnEdit()
    {
        var session = new ProjectSession(new DelayedStore());
        session.New(new(), markDirty: false);
        Assert.False(session.IsDirty);
        session.Update(session.Project with { Name = session.Project.Name });
        Assert.False(session.IsDirty);
        session.Update(session.Project with { Name = "Changed" });
        Assert.True(session.IsDirty);
    }
    [Fact] public async Task EditsDuringSaveRemainDirty()
    {
        var store = new DelayedStore();
        var session = new ProjectSession(store);
        session.New(new() { Name = "Before" });
        var save = session.SaveAsync("project", CancellationToken.None);
        session.Update(session.Project with { Name = "During save" });
        store.Complete.SetResult();
        await save;
        Assert.True(session.IsDirty);
        Assert.Equal("Before", store.Snapshot!.Name);
    }

    [Fact] public async Task FailedOpenPreservesCurrentProject()
    {
        var session = new ProjectSession(new DelayedStore());
        session.New(new() { Name = "Keep me" });
        await Assert.ThrowsAsync<IOException>(() => session.OpenAsync("missing", CancellationToken.None));
        Assert.Equal("Keep me", session.Project.Name);
        Assert.True(session.IsDirty);
    }

    private sealed class DelayedStore : IProjectStore
    {
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DubbingProject? Snapshot { get; private set; }
        public Task<DubbingProject> LoadAsync(string directory, CancellationToken cancellationToken) => throw new IOException("Missing");
        public async Task SaveAsync(DubbingProject project, string directory, CancellationToken cancellationToken)
        {
            Snapshot = project;
            await Complete.Task.WaitAsync(cancellationToken);
        }
    }
}
