"""{name}."""

import argparse


def main():
    parser = argparse.ArgumentParser(description="{name}")
    parser.add_argument("--verbose", action="store_true", help="print more")
    args = parser.parse_args()
    print("Hello from {file}", args)


if __name__ == "__main__":
    main()
