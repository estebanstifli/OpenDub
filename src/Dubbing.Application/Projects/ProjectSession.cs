using Dubbing.Core.Projects;

namespace Dubbing.Application.Projects;

public sealed class ProjectSession(IProjectStore store)
{
    private long revision;
    private long savedRevision;
    public DubbingProject Project { get; private set; } = new();
    public string? DirectoryPath { get; private set; }
    public bool IsDirty => revision != savedRevision;

    public void New(DubbingProject project)
    {
        Project = project;
        DirectoryPath = null;
        revision++;
        savedRevision = revision - 1;
    }

    public void Update(DubbingProject project)
    {
        if (ReferenceEquals(project, Project)) return;
        Project = project;
        revision++;
    }

    public async Task OpenAsync(string directory, CancellationToken cancellationToken)
    {
        var project = await store.LoadAsync(directory, cancellationToken);
        Project = project;
        DirectoryPath = directory;
        savedRevision = ++revision;
    }

    public async Task SaveAsync(string? directory, CancellationToken cancellationToken)
    {
        var target = directory ?? DirectoryPath ?? throw new InvalidOperationException("Choose a project folder.");
        var snapshot = Project;
        long savingRevision = revision;
        await store.SaveAsync(snapshot, target, cancellationToken);
        // A completed old save must not mark a subsequently edited or replaced project clean.
        if (Project.Id == snapshot.Id)
        {
            DirectoryPath = target;
            if (revision == savingRevision) savedRevision = savingRevision;
        }
    }
}
