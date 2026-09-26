# Prints the argparse add_argument calls of the script named in argv[1] as JSON, without running it.
import ast
import json
import sys


def literal(node):
    try:
        return ast.literal_eval(node)
    except Exception:
        return None


def type_name(node):
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        return node.attr
    return None


def main():
    with open(sys.argv[1], encoding="utf-8-sig") as f:
        tree = ast.parse(f.read())
    found = []
    for node in ast.walk(tree):
        if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == "add_argument"):
            continue
        names = [literal(a) for a in node.args]
        if not names or not all(isinstance(n, str) for n in names):
            continue
        entry = {"names": names}
        for keyword in node.keywords:
            if keyword.arg == "type":
                entry["type"] = type_name(keyword.value)
            elif keyword.arg == "default":
                entry["default"] = literal(keyword.value)
            elif keyword.arg in ("action", "choices", "help", "nargs", "dest", "required"):
                entry[keyword.arg] = literal(keyword.value)
        found.append(entry)
    json.dump(found, sys.stdout, default=str)


main()
