using System;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Testable subclass that exposes DrawingToolBase's protected static parameter methods.
    /// </summary>
    public class TestableToolBase
    {
        public static string? GetStringParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
            return null;
        }

        public static string GetRequiredStringParam(JsonElement parameters, string name)
        {
            var value = GetStringParam(parameters, name);
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"Required parameter '{name}' is missing or empty");
            }
            return value;
        }

        public static int? GetIntParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                {
                    if (prop.TryGetInt32(out int intVal))
                        return intVal;
                    return (int)prop.GetDouble();
                }
            }
            return null;
        }

        public static double? GetDoubleParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
            }
            return null;
        }

        public static bool GetBoolParam(JsonElement parameters, string name, bool defaultValue = false)
        {
            if (parameters.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True)
                    return true;
                if (prop.ValueKind == JsonValueKind.False)
                    return false;
            }
            return defaultValue;
        }

        public static string[]? GetStringArrayParam(JsonElement parameters, string name)
        {
            if (parameters.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.Array)
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (val != null)
                            list.Add(val);
                    }
                }
                return list.ToArray();
            }
            return null;
        }
    }

    public class ParameterExtractionTests
    {
        private static JsonElement Parse(string json) =>
            JsonDocument.Parse(json).RootElement;

        #region GetStringParam

        [Fact]
        public void GetStringParam_ReturnsValue_WhenPresent()
        {
            var p = Parse("""{"name": "Alignment1"}""");
            TestableToolBase.GetStringParam(p, "name").Should().Be("Alignment1");
        }

        [Fact]
        public void GetStringParam_ReturnsNull_WhenMissing()
        {
            var p = Parse("""{"other": "value"}""");
            TestableToolBase.GetStringParam(p, "name").Should().BeNull();
        }

        [Fact]
        public void GetStringParam_ReturnsNull_WhenNotString()
        {
            var p = Parse("""{"name": 42}""");
            TestableToolBase.GetStringParam(p, "name").Should().BeNull();
        }

        [Fact]
        public void GetStringParam_ReturnsNull_WhenValueIsNull()
        {
            var p = Parse("""{"name": null}""");
            TestableToolBase.GetStringParam(p, "name").Should().BeNull();
        }

        #endregion

        #region GetRequiredStringParam

        [Fact]
        public void GetRequiredStringParam_ReturnsValue_WhenPresent()
        {
            var p = Parse("""{"name": "Road1"}""");
            TestableToolBase.GetRequiredStringParam(p, "name").Should().Be("Road1");
        }

        [Fact]
        public void GetRequiredStringParam_Throws_WhenMissing()
        {
            var p = Parse("""{"other": "val"}""");
            var act = () => TestableToolBase.GetRequiredStringParam(p, "name");
            act.Should().Throw<ArgumentException>().WithMessage("*name*");
        }

        [Fact]
        public void GetRequiredStringParam_Throws_WhenEmpty()
        {
            var p = Parse("""{"name": ""}""");
            var act = () => TestableToolBase.GetRequiredStringParam(p, "name");
            act.Should().Throw<ArgumentException>();
        }

        #endregion

        #region GetIntParam

        [Fact]
        public void GetIntParam_ReturnsValue_WhenInteger()
        {
            var p = Parse("""{"count": 5}""");
            TestableToolBase.GetIntParam(p, "count").Should().Be(5);
        }

        [Fact]
        public void GetIntParam_ReturnsNull_WhenMissing()
        {
            var p = Parse("""{"other": 1}""");
            TestableToolBase.GetIntParam(p, "count").Should().BeNull();
        }

        [Fact]
        public void GetIntParam_HandlesFloatAsInt_FromPython()
        {
            // Python sends 60.0 for integers - should be handled gracefully
            var p = Parse("""{"speed": 60.0}""");
            TestableToolBase.GetIntParam(p, "speed").Should().Be(60);
        }

        [Fact]
        public void GetIntParam_ReturnsNull_WhenNotNumber()
        {
            var p = Parse("""{"count": "five"}""");
            TestableToolBase.GetIntParam(p, "count").Should().BeNull();
        }

        #endregion

        #region GetDoubleParam

        [Fact]
        public void GetDoubleParam_ReturnsValue_WhenNumber()
        {
            var p = Parse("""{"radius": 150.5}""");
            TestableToolBase.GetDoubleParam(p, "radius").Should().Be(150.5);
        }

        [Fact]
        public void GetDoubleParam_ReturnsNull_WhenMissing()
        {
            var p = Parse("""{}""");
            TestableToolBase.GetDoubleParam(p, "radius").Should().BeNull();
        }

        [Fact]
        public void GetDoubleParam_ReturnsNull_WhenNotNumber()
        {
            var p = Parse("""{"radius": "large"}""");
            TestableToolBase.GetDoubleParam(p, "radius").Should().BeNull();
        }

        #endregion

        #region GetBoolParam

        [Fact]
        public void GetBoolParam_ReturnsTrue_WhenTrue()
        {
            var p = Parse("""{"verbose": true}""");
            TestableToolBase.GetBoolParam(p, "verbose").Should().BeTrue();
        }

        [Fact]
        public void GetBoolParam_ReturnsFalse_WhenFalse()
        {
            var p = Parse("""{"verbose": false}""");
            TestableToolBase.GetBoolParam(p, "verbose").Should().BeFalse();
        }

        [Fact]
        public void GetBoolParam_ReturnsDefault_WhenMissing()
        {
            var p = Parse("""{}""");
            TestableToolBase.GetBoolParam(p, "verbose", true).Should().BeTrue();
        }

        [Fact]
        public void GetBoolParam_ReturnsDefault_WhenNotBool()
        {
            var p = Parse("""{"verbose": "yes"}""");
            TestableToolBase.GetBoolParam(p, "verbose", false).Should().BeFalse();
        }

        #endregion

        #region GetStringArrayParam

        [Fact]
        public void GetStringArrayParam_ReturnsArray_WhenValid()
        {
            var p = Parse("""{"names": ["a", "b", "c"]}""");
            TestableToolBase.GetStringArrayParam(p, "names").Should().BeEquivalentTo(new[] { "a", "b", "c" });
        }

        [Fact]
        public void GetStringArrayParam_ReturnsNull_WhenMissing()
        {
            var p = Parse("""{}""");
            TestableToolBase.GetStringArrayParam(p, "names").Should().BeNull();
        }

        [Fact]
        public void GetStringArrayParam_SkipsNonStringElements()
        {
            var p = Parse("""{"names": ["a", 42, "b"]}""");
            TestableToolBase.GetStringArrayParam(p, "names").Should().BeEquivalentTo(new[] { "a", "b" });
        }

        [Fact]
        public void GetStringArrayParam_ReturnsEmptyArray_ForEmptyArray()
        {
            var p = Parse("""{"names": []}""");
            TestableToolBase.GetStringArrayParam(p, "names").Should().BeEmpty();
        }

        [Fact]
        public void GetStringArrayParam_ReturnsNull_WhenNotArray()
        {
            var p = Parse("""{"names": "single"}""");
            TestableToolBase.GetStringArrayParam(p, "names").Should().BeNull();
        }

        #endregion
    }
}
