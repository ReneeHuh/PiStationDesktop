"""Validate source GraphQL documents against an explicitly supplied GitHub schema.

Requires graphql-core. Download the current schema from:
https://docs.github.com/public/fpt/schema.docs.graphql
Usage: python validate_graphql.py path/to/schema.docs.graphql
"""

import pathlib
import re
import sys

from graphql import build_schema, parse, validate


def source_documents(root):
    for path in sorted(root.glob("SourceControlHostingService*.cs")):
        source = path.read_text(encoding="utf-8-sig")
        for match in re.finditer(r'(\w+)\s*=\s*"""\s*(.*?)"""', source, re.S):
            if match[2].startswith(("query", "mutation")):
                yield f"{path.name}:{match[1]}", match[2]
        for match in re.finditer(r'(?<![$])"(mutation\([^"\n]*?)"', source):
            yield f"{path.name}:{source[:match.start()].count(chr(10)) + 1}", match[1]

        # Expand the comment mutation templates using the adjacent switch arms.
        variants = re.findall(r'=>\s*\("(\w+)", "(\w+)"\)', source)
        templates = re.findall(r'\$"(mutation\([^"\n]*?)"', source)
        for template in templates:
            if "{mutation}" in template:
                choices = re.search(r'var mutation = .*?\? "(\w+)" : "(\w+)";', source)
                if choices is None:
                    raise ValueError(f"Missing mutation choices in {path.name}")
                for name in choices.groups():
                    document = template.replace("{mutation}", name)
                    yield f"{path.name}:{name}", document.replace("{{", "{").replace("}}", "}")
                continue
            if "{name}" not in template:
                raise ValueError(f"Unrecognized mutation template in {path.name}: {template}")
            deleting = "$body" not in template
            for name, id_field in variants:
                if name.startswith("delete") != deleting:
                    continue
                document = template.replace("{name}", name).replace("{idField}", id_field)
                yield f"{path.name}:{name}", document.replace("{{", "{").replace("}}", "}")


if len(sys.argv) != 2:
    raise SystemExit("Usage: python validate_graphql.py <GitHub-schema.graphql>")
schema = build_schema(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
root = pathlib.Path(__file__).resolve().parents[2] / "src/PiStation.Host/SourceControl"
documents = list(source_documents(root))
if not documents:
    raise SystemExit("No GraphQL documents found.")
errors = []
for label, document in documents:
    try:
        errors.extend(f"{label}: {error.message}" for error in validate(schema, parse(document)))
    except Exception as error:
        errors.append(f"{label}: {error}")
print(f"Validated {len(documents)} GraphQL documents; {len(errors)} errors.")
for error in errors:
    print(error)
raise SystemExit(bool(errors))
