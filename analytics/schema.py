"""Plan additive nullable projections; never update existing evidence."""


def nullable_additions(existing, incoming):
    """Input maps contain field names and canonical Spark simpleString types."""
    additions = {}
    for name, datatype in incoming.items():
        if name in existing and existing[name] != datatype:
            raise ValueError("Unsafe type change in " + name)
        if name not in existing:
            additions[name] = datatype
    return additions
