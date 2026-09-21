"""Mark objects as existing building underlay on X-EXIST."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def mark_as_existing(
    ctx: Context,
    ids: Optional[List[str]] = None,
    target_layer: str = "X-EXIST",
) -> Dict[str, Any]:
    """
    Mark the selection (or explicit ids) as the existing building.

    Moves each object onto X-EXIST (created if missing), stamps
    forsk:kind=existing and forsk:id x01…, and removes forsk:generated.
    Kind is overwritten even if the object was previously a wall.
    clear_generated does not delete these objects.

    Parameters:
    - ids: Optional GUIDs. Omit to use the current selection.
    - target_layer: Layer to move onto (default "X-EXIST").

    Returns:
    Dictionary with ids, forsk_ids, count, target_layer, and message.

    Empty selection and no ids refuses with:
    Nothing is selected. Select the existing building, then mark it again.
    """
    try:
        if ids is not None and not isinstance(ids, list):
            return {"success": False, "message": "ids must be a list of GUIDs."}
        if not isinstance(target_layer, str) or not target_layer.strip():
            return {"success": False, "message": "target_layer must be a layer name."}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {"target_layer": target_layer.strip()}
        if ids:
            params["ids"] = ids

        result = rhino.send_command("mark_as_existing", params)
        return {
            "success": True,
            "ids": result.get("ids", []),
            "forsk_ids": result.get("forsk_ids", []),
            "count": result.get("count", 0),
            "target_layer": result.get("target_layer", target_layer),
            "message": result.get("message", "Marked existing"),
        }
    except Exception as e:
        logger.error(f"Error in mark_as_existing: {str(e)}")
        return {"success": False, "message": str(e)}
