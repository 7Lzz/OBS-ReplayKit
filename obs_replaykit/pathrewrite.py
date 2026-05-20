"""rewrite hardcoded C:\\Users\\<name> paths to the current user."""

import re

# shared/system accounts we never want to rewrite.
_SHARED_USER_FOLDERS = frozenset({"public", "default", "all users", "default user"})

# matches c:\users\<x>, c:/users/<x>, or escaped c:\\users\\<x>; captures the prefix + username separately.
_USER_PATH_RE = re.compile(
    r'(C:[\\/]+Users[\\/]+)([^\\/\s"\']+)', flags=re.IGNORECASE
)


def rewrite_user_paths(text: str, new_user: str) -> str:
    """replace every c:\\users\\<x> with c:\\users\\<new_user>. preserves the separator style (single backslash, escaped backslash, or forward slash) and skips shared accounts."""
    def _repl(match: re.Match) -> str:
        prefix, found = match.group(1), match.group(2)
        if found.lower() in _SHARED_USER_FOLDERS:
            return match.group(0)
        return f"{prefix}{new_user}"

    return _USER_PATH_RE.sub(_repl, text)
