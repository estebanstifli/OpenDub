using Dubbing.Core.Media;
using Dubbing.Core.Engines;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Models;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.App.Services;

public sealed record WorkspaceServices(IMediaEngine Media, SettingsStore Settings, ModelCatalog Catalog,
    IModelManager Models, IRuntimeLocator Locator, IProcessRunner Processes, RuntimeCoordinator Runtimes);
