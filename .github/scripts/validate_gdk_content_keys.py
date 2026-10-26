"""Validate required GDK build configuration without displaying key material."""

import os
import re
import sys
from collections.abc import Mapping


CONTENT_KEYS = {
    "PCL_BEDROCK_RELEASE_CIK": "BEDROCK_RELEASE_CIK",
    "PCL_BEDROCK_PREVIEW_CIK": "BEDROCK_PREVIEW_CIK",
}
MINIMUM_HEX_CHARACTERS = 48 * 2


def configuration_errors(environment: Mapping[str, str]) -> list[str]:
    errors = []
    # The source generator checks presence, not a Boolean interpretation of this value.
    if "PCL_WRITE_SECRET" not in environment:
        errors.append("PCL_WRITE_SECRET: enable build-time injection by setting this environment variable (the publishing workflow uses 1).")

    for variable, repository_secret in CONTENT_KEYS.items():
        value = environment.get(variable)
        if not value:
            errors.append(f"{variable}: configure the {repository_secret} repository secret and map it into the publishing environment.")
        elif re.fullmatch(r"[0-9A-Fa-f]+", value) is None:
            errors.append(f"{variable}: use bare ASCII hexadecimal with no prefix, quotes, whitespace, or separators; update {repository_secret}.")
        elif len(value) % 2:
            errors.append(f"{variable}: use an even number of hexadecimal characters; update {repository_secret}.")
        elif len(value) < MINIMUM_HEX_CHARACTERS:
            errors.append(f"{variable}: provide at least 48 bytes (96 hexadecimal characters); update {repository_secret}.")
    return errors


def main() -> int:
    errors = configuration_errors(os.environ)
    for error in errors:
        # Diagnostics contain only constant variable names and corrective instructions.
        print(f"::error::{error}", file=sys.stderr)
    if errors:
        return 1
    print("GDK content-key build configuration passed format checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
