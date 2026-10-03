using System.Text.Json.Nodes;
using TravelRepo.Core;
namespace TravelRepo.Repository;

public sealed partial class TravelRepository
{
    public async Task<Entity> ReplaceDocumentAsync(Guid id, string source, string mediaType, bool allowLarge = false, CancellationToken ct = default)
    {
        if (new FileInfo(source).Length > 25 * 1024 * 1024 && !allowLarge) throw new DomainException("asset.large", "This file exceeds 25 MB. Confirm before adding it to trip history.");
        var bytes = await File.ReadAllBytesAsync(source, ct); var hash = Hash(bytes); var path = "assets/sha256/" + hash[..2] + "/" + hash;
        var state = await ReadAsync(ct); var doc = state.Trip.Find(id)?.Copy() ?? throw new DomainException("document.missing", "Document not found.");
        if (doc.Type != "document") throw new DomainException("document.type", "Only a document can have its binary replaced.");
        doc.Data["blob"] = new JsonObject { ["algorithm"] = "sha256", ["hash"] = hash }; doc.Data["media_type"] = mediaType;
        await ApplyAsync(state, [new(id, doc)], ct, [new(path, bytes)]); return doc;
    }
}
