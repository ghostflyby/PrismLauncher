// SPDX-License-Identifier: GPL-3.0-only
// Port of Json::JsonException (aliased JSONValidationError upstream). Formerly nested in
// the JsonUtils static class; it stands alone since that node-helper infrastructure was
// fully migrated onto the typed wire readers (docs/JSON-MIGRATION.md).

namespace FlintLauncher.Core.Json;

public sealed class JsonParseException(string message) : Exception(message)
{
}
