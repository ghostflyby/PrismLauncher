// SPDX-License-Identifier: GPL-3.0-only
// Port of the data half of ui/pages/modplatform/ftb/FtbListModel.{h,cpp} (+ the
// FtbFilterModel search semantics): fetch /modpack/all, then each pack description
// (/modpack/{id}) in sequence, dropping the "dud" packs without versions. The Qt
// QAbstractListModel/logos machinery is not ported — this is the plain data layer.
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Ftb;

public sealed class FtbPackModel
{
    public FtbPackModel(FtbApi? ftb = null)
    {
        _ftb = ftb ?? new FtbApi();
    }

    private readonly FtbApi _ftb;
    private readonly List<FtbModpack> _modpacks = [];

    public IReadOnlyList<FtbModpack> Packs => _modpacks;

    /// <summary>Packs visible under the current search term (FtbFilterModel::filterAcceptsRow:
    /// case-insensitive name contains; empty term accepts everything).</summary>
    public IEnumerable<FtbModpack> FilteredPacks(string searchTerm)
    {
        searchTerm = searchTerm.Trim();
        if (searchTerm.Length == 0)
        {
            return _modpacks;
        }
        return _modpacks.Where(pack =>
            pack.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Port of ListModel::request + requestPack: fetch the index, then fetch every pack
    /// description in sequence. Packs without any version are ignored (upstream logs
    /// "lacking any versions"); a pack whose response cannot be understood is skipped like
    /// the C++ parse-error path (it only logs and continues).
    /// </summary>
    public async Task FetchAsync(CancellationToken cancellationToken = default)
    {
        _modpacks.Clear();

        var (indexJob, indexRequest) = _ftb.FetchModpacks();
        await indexJob.StartAsync(cancellationToken);
        if (!indexJob.WasSuccessful)
        {
            // (ListModel::requestFailed: the model stays empty)
            return;
        }

        List<int> remainingPacks;
        try
        {
            remainingPacks = FtbApi.LoadModpackIndex(indexRequest.BodyAsString);
        }
        catch (Json.JsonParseException)
        {
            // (the C++ only warns and leaves the model empty)
            return;
        }

        while (remainingPacks.Count != 0)
        {
            var currentPack = remainingPacks[0];
            var (job, request) = _ftb.FetchModpack(currentPack);
            await job.StartAsync(cancellationToken);
            remainingPacks.Remove(currentPack);
            if (!job.WasSuccessful)
            {
                // (ListModel::packRequestFailed: drop the pack and continue)
                continue;
            }

            try
            {
                var pack = FtbApi.LoadModpack(request.BodyAsString);

                // Since there is no guarantee that packs have a version, this will just
                // ignore those "dud" packs.
                if (pack.Versions.Count == 0)
                {
                    continue;
                }
                _modpacks.Add(pack);
            }
            catch (Json.JsonParseException)
            {
                // (the C++ only logs "Error while reading pack manifest from FTB")
            }
        }
    }
}
