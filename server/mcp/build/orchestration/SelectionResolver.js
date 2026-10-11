function asSelectedObjects(projectContext) {
    return Array.isArray(projectContext.selectedObjects)
        ? projectContext.selectedObjects
        : [];
}
function findSelectedNameByType(projectContext, objectTypeMatch) {
    const matches = asSelectedObjects(projectContext).filter((item) => {
        const objectType = item.objectType?.toLowerCase() ?? "";
        return objectType.includes(objectTypeMatch);
    });
    if (matches.length !== 1) {
        return undefined;
    }
    const selectedName = matches[0]?.name?.trim();
    return selectedName && selectedName.length > 0 ? selectedName : undefined;
}
function findFirstSelectedName(projectContext, objectTypeMatches) {
    for (const objectTypeMatch of objectTypeMatches) {
        const selectedName = findSelectedNameByType(projectContext, objectTypeMatch);
        if (selectedName) {
            return selectedName;
        }
    }
    return undefined;
}
export function resolveParamsFromSelection(params, projectContext) {
    const resolvedParams = { ...params };
    const inferredFromSelection = [];
    if (!resolvedParams.alignmentName) {
        const selectedAlignmentName = findSelectedNameByType(projectContext, "alignment");
        if (selectedAlignmentName) {
            resolvedParams.alignmentName = selectedAlignmentName;
            inferredFromSelection.push("alignmentName");
        }
    }
    if (!resolvedParams.surfaceName) {
        const selectedSurfaceName = findSelectedNameByType(projectContext, "surface");
        if (selectedSurfaceName) {
            resolvedParams.surfaceName = selectedSurfaceName;
            inferredFromSelection.push("surfaceName");
        }
    }
    if (!resolvedParams.name) {
        const selectedObjectName = findFirstSelectedName(projectContext, ["surface", "alignment", "corridor"]);
        if (selectedObjectName) {
            resolvedParams.name = selectedObjectName;
            inferredFromSelection.push("name");
        }
    }
    return {
        resolvedParams,
        inferredFromSelection,
    };
}
