namespace Horus.Domain.Models
{
    public class ProtocolParam
    {
        public string Key { get; init; }
        public string Label { get; init; }
        public ParamType ParamType { get; init; }   // String | Int | Bool | Select
        public object DefaultValue { get; init; }
        public string[]? Options { get; init; }
        public bool IsRequired { get; init; }
    }
}
