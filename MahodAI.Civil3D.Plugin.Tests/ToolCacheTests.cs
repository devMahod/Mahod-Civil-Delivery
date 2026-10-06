using System;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class ToolCacheTests
    {
        #region Basic Get/Set

        [Fact]
        public void TryGet_ReturnsFalse_ForCacheMiss()
        {
            var cache = new ToolCache();
            cache.TryGet<string>("missing", out var value).Should().BeFalse();
            value.Should().BeNull();
        }

        [Fact]
        public void TryGet_ReturnsTrue_ForCacheHit()
        {
            var cache = new ToolCache();
            cache.Set("key1", "value1");
            cache.TryGet<string>("key1", out var value).Should().BeTrue();
            value.Should().Be("value1");
        }

        [Fact]
        public void TryGet_ReturnsFalse_WhenTypeMismatch()
        {
            var cache = new ToolCache();
            cache.Set("key1", 42);
            cache.TryGet<string>("key1", out var value).Should().BeFalse();
            value.Should().BeNull();
        }

        [Fact]
        public void Set_OverwritesExistingEntry()
        {
            var cache = new ToolCache();
            cache.Set("key1", "old");
            cache.Set("key1", "new");
            cache.TryGet<string>("key1", out var value).Should().BeTrue();
            value.Should().Be("new");
        }

        [Fact]
        public void Count_ReflectsNumberOfEntries()
        {
            var cache = new ToolCache();
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Count.Should().Be(2);
        }

        #endregion

        #region Expiration

        [Fact]
        public void TryGet_ReturnsFalse_WhenEntryExpired()
        {
            var cache = new ToolCache();
            cache.Set("key1", "value1", TimeSpan.FromMilliseconds(1));
            Thread.Sleep(50);
            cache.TryGet<string>("key1", out _).Should().BeFalse();
        }

        [Fact]
        public void TryGet_ReturnsTrue_WhenEntryNotYetExpired()
        {
            var cache = new ToolCache();
            cache.Set("key1", "value1", TimeSpan.FromMinutes(5));
            cache.TryGet<string>("key1", out var value).Should().BeTrue();
            value.Should().Be("value1");
        }

        #endregion

        #region LRU Eviction

        [Fact]
        public void Eviction_RemovesLRUEntries_WhenOverCapacity()
        {
            var cache = new ToolCache(maxEntries: 3);
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Set("c", 3);

            // Access "a" to make it recently used
            cache.TryGet<int>("a", out _);

            // Adding a 4th entry should trigger eviction
            cache.Set("d", 4);

            // "a" was accessed most recently, "d" is newest - "b" was LRU
            cache.TryGet<int>("a", out _).Should().BeTrue();
            cache.TryGet<int>("d", out _).Should().BeTrue();
        }

        [Fact]
        public void Eviction_RemovesExpiredFirst()
        {
            var cache = new ToolCache(maxEntries: 3);
            cache.Set("expired", "old", TimeSpan.FromMilliseconds(1));
            cache.Set("b", 2);
            cache.Set("c", 3);

            Thread.Sleep(50);

            // Adding a 4th entry triggers eviction; expired entry should be removed first
            cache.Set("d", 4);
            cache.TryGet<string>("expired", out _).Should().BeFalse();
        }

        #endregion

        #region Remove / RemoveByPattern

        [Fact]
        public void Remove_RemovesExistingEntry()
        {
            var cache = new ToolCache();
            cache.Set("key1", "val");
            cache.Remove("key1").Should().BeTrue();
            cache.TryGet<string>("key1", out _).Should().BeFalse();
        }

        [Fact]
        public void Remove_ReturnsFalse_ForMissingKey()
        {
            var cache = new ToolCache();
            cache.Remove("missing").Should().BeFalse();
        }

        [Fact]
        public void RemoveByPattern_RemovesMatchingEntries()
        {
            var cache = new ToolCache();
            cache.Set("alignment:123:data", "a");
            cache.Set("alignment:456:data", "b");
            cache.Set("surface:789:data", "c");

            int removed = cache.RemoveByPattern("alignment");
            removed.Should().Be(2);
            cache.Count.Should().Be(1);
        }

        [Fact]
        public void RemoveByPattern_IsCaseInsensitive()
        {
            var cache = new ToolCache();
            cache.Set("Alignment:123", "a");
            cache.RemoveByPattern("alignment").Should().Be(1);
        }

        [Fact]
        public void InvalidateObject_RemovesEntriesContainingObjectId()
        {
            var cache = new ToolCache();
            cache.Set("tool1:OBJ123:result", "a");
            cache.Set("tool2:OBJ123:result", "b");
            cache.Set("tool1:OBJ999:result", "c");

            cache.InvalidateObject("OBJ123").Should().Be(2);
            cache.Count.Should().Be(1);
        }

        [Fact]
        public void InvalidateTool_RemovesEntriesForTool()
        {
            var cache = new ToolCache();
            cache.Set("get_alignments:abc", "a");
            cache.Set("get_alignments:def", "b");
            cache.Set("get_profiles:abc", "c");

            cache.InvalidateTool("get_alignments").Should().Be(2);
            cache.Count.Should().Be(1);
        }

        #endregion

        #region Clear / GetOrCreate

        [Fact]
        public void Clear_RemovesAllEntries()
        {
            var cache = new ToolCache();
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Clear();
            cache.Count.Should().Be(0);
        }

        [Fact]
        public void GetOrCreate_ReturnsExistingValue()
        {
            var cache = new ToolCache();
            cache.Set("key1", "existing");
            var result = cache.GetOrCreate("key1", () => "new");
            result.Should().Be("existing");
        }

        [Fact]
        public void GetOrCreate_CreatesAndCachesNewValue()
        {
            var cache = new ToolCache();
            var result = cache.GetOrCreate("key1", () => "created");
            result.Should().Be("created");
            cache.TryGet<string>("key1", out var cached).Should().BeTrue();
            cached.Should().Be("created");
        }

        #endregion

        #region Statistics

        [Fact]
        public void GetStatistics_ReturnsCorrectCounts()
        {
            var cache = new ToolCache();
            cache.Set("a", 1);
            cache.Set("b", 2);

            // Access "a" to generate a hit
            cache.TryGet<int>("a", out _);

            var stats = cache.GetStatistics();
            stats.TotalEntries.Should().Be(2);
            stats.ValidEntries.Should().Be(2);
            stats.TotalHits.Should().Be(1);
        }

        [Fact]
        public void GetStatistics_CountsExpiredEntries()
        {
            var cache = new ToolCache();
            cache.Set("expired", 1, TimeSpan.FromMilliseconds(1));
            cache.Set("valid", 2, TimeSpan.FromMinutes(5));

            Thread.Sleep(50);

            var stats = cache.GetStatistics();
            stats.TotalEntries.Should().Be(2);
            stats.ValidEntries.Should().Be(1);
            stats.ExpiredEntries.Should().Be(1);
        }

        #endregion
    }
}
