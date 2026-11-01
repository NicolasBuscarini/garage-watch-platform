using GarageWatch.Application.Analysis;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class FaceEmbeddingContractTests
{
    [Fact]
    public async Task Versioned_extractor_contract_accepts_bounded_test_result_without_persisting_biometrics()
    {
        var extractor = new SyntheticFaceEmbeddingExtractor();
        var crop = new FaceCropV1("image/jpeg", 160, 160, new byte[] { 1, 2, 3 });

        var embedding = await extractor.ExtractAsync(crop, CancellationToken.None);

        Assert.True(crop.IsValid);
        Assert.True(embedding.IsValid);
        Assert.Equal(FaceEmbeddingV1.CurrentSchemaVersion, embedding.SchemaVersion);
        Assert.Equal("test-face-model", embedding.ModelId);
        Assert.Equal(3, embedding.Dimensions);
        Assert.False(new FaceCropV1("image/jpeg", 4096, 160, new byte[] { 1 }).IsValid);
        Assert.False((embedding with { Values = [float.NaN, 0, 1] }).IsValid);
    }

    private sealed class SyntheticFaceEmbeddingExtractor : IFaceEmbeddingExtractor
    {
        public string ModelId => "test-face-model";
        public string ModelVersion => "contract-v1";

        public ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!crop.IsValid) throw new ArgumentException("Invalid test crop.", nameof(crop));
            return ValueTask.FromResult(new FaceEmbeddingV1(FaceEmbeddingV1.CurrentSchemaVersion,
                ModelId, ModelVersion, 3, [0.1f, 0.2f, 0.3f]));
        }
    }
}
