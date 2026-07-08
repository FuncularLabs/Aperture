using Reel.Core.Annotations;

namespace Reel.Core.Tests;

public class AnnotationMergeTests
{
    // --- MergeTags ---

    [Fact]
    public void MergeTags_AddsAndRemoves()
    {
        var result = AnnotationMerge.MergeTags(["a", "b"], added: ["c"], removed: ["b"]);
        Assert.Equal(["a", "c"], result);
    }

    [Fact]
    public void MergeTags_IsCaseInsensitive_NoDuplicates()
    {
        // Adding a tag the item already has (different case) does not duplicate it.
        var result = AnnotationMerge.MergeTags(["Party"], added: ["party"], removed: []);
        Assert.Equal(["Party"], result);
    }

    [Fact]
    public void MergeTags_RemoveHonorsCaseInsensitive()
    {
        var result = AnnotationMerge.MergeTags(["Beach", "Sun"], added: [], removed: ["beach"]);
        Assert.Equal(["Sun"], result);
    }

    [Fact]
    public void MergeTags_RemovalWinsOverAddition()
    {
        var result = AnnotationMerge.MergeTags(["x"], added: ["y"], removed: ["y"]);
        Assert.Equal(["x"], result);
    }

    [Fact]
    public void MergeTags_TrimsAndDropsBlanks()
    {
        var result = AnnotationMerge.MergeTags(["  a  ", ""], added: ["  b "], removed: []);
        Assert.Equal(["a", "b"], result);
    }

    // --- MergeNote ---

    [Fact]
    public void MergeNote_Unchanged_KeepsOriginal()
    {
        Assert.Equal("keep me", AnnotationMerge.MergeNote("keep me", editedNote: "ignored", majorityNote: "keep me", noteChanged: false));
    }

    [Fact]
    public void MergeNote_Changed_AppliesToMajorityMatch()
    {
        Assert.Equal("new", AnnotationMerge.MergeNote("old", editedNote: "new", majorityNote: "old", noteChanged: true));
    }

    [Fact]
    public void MergeNote_Changed_AppliesToEmptyItem()
    {
        Assert.Equal("new", AnnotationMerge.MergeNote("", editedNote: "new", majorityNote: "old", noteChanged: true));
    }

    [Fact]
    public void MergeNote_Changed_ExcludesDifferentNote()
    {
        // An item whose note differs from the majority is left untouched.
        Assert.Equal("mine", AnnotationMerge.MergeNote("mine", editedNote: "new", majorityNote: "old", noteChanged: true));
    }
}
