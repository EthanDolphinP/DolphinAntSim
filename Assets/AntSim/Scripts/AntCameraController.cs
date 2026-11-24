using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// Top-down RTS camera: WASD/arrows or right/middle-drag to pan, mouse wheel to zoom at the cursor.
    /// Keyboard panning eases in and out and the zoom glides; dragging stays locked to the mouse.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class AntCameraController : MonoBehaviour
    {
        public AntWorld world;
        public float panSpeed = 1.2f;   // screen-heights per second
        public float zoomStep = 0.88f;
        public float minSize = 4f;
        [Tooltip("How quickly panning and zooming catch up (per second). Higher is snappier.")]
        public float smoothing = 12f;

        Camera cam;
        Vector3 lastMouse;
        Vector3 panVelocity;
        float targetSize;
        float lastSetSize;
        Vector3 zoomScreenPoint;

        void Awake()
        {
            cam = GetComponent<Camera>();
            targetSize = lastSetSize = cam.orthographicSize;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float ease = 1f - Mathf.Exp(-smoothing * dt);
            float maxSize = world != null ? Mathf.Max(world.Grid.WorldSize.x, world.Grid.WorldSize.y) * 0.75f : 1000f;

            // Someone else moved the zoom (new game, focus on a colony): take it as the new target.
            if (!Mathf.Approximately(cam.orthographicSize, lastSetSize)) targetSize = cam.orthographicSize;

            // No WASD panning while typing (a save name, say) or while the Esc menu is open.
            bool blocked = AntSimHud.IsTyping || AntSimHud.MenuOpen;
            var move = blocked ? Vector3.zero
                : new Vector3(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"), 0f);
            if (!blocked && GameSettings.EdgePan && Application.isFocused)
            {
                // Scroll when the mouse touches the edge of the screen (Settings).
                var m = Input.mousePosition;
                const float edge = 6f;
                if (m.x >= 0 && m.x <= edge) move.x = -1f; else if (m.x >= Screen.width - edge && m.x <= Screen.width) move.x = 1f;
                if (m.y >= 0 && m.y <= edge) move.y = -1f; else if (m.y >= Screen.height - edge && m.y <= Screen.height) move.y = 1f;
            }
            panSpeed = GameSettings.PanSpeed;
            if (move.sqrMagnitude > 1f) move.Normalize();
            panVelocity = Vector3.Lerp(panVelocity, move * (cam.orthographicSize * panSpeed), ease);
            transform.position += panVelocity * dt;

            if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)) lastMouse = Input.mousePosition;
            if (!blocked && (Input.GetMouseButton(1) || Input.GetMouseButton(2)))
            {
                Vector3 a = cam.ScreenToWorldPoint(lastMouse);
                Vector3 b = cam.ScreenToWorldPoint(Input.mousePosition);
                transform.position += a - b;
                lastMouse = Input.mousePosition;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (scroll != 0f && !blocked && !AntSimHud.IsMouseOverUi())
            {
                targetSize = Mathf.Clamp(targetSize * Mathf.Pow(zoomStep, scroll * GameSettings.ZoomSpeed), minSize, maxSize);
                zoomScreenPoint = Input.mousePosition;
            }
            if (Mathf.Abs(cam.orthographicSize - targetSize) > targetSize * 0.0005f)
            {
                // Glide toward the target zoom, keeping the point under the cursor where it was.
                Vector3 before = cam.ScreenToWorldPoint(zoomScreenPoint);
                cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, targetSize, ease);
                Vector3 after = cam.ScreenToWorldPoint(zoomScreenPoint);
                transform.position += before - after;
            }
            else cam.orthographicSize = targetSize;
            lastSetSize = cam.orthographicSize;

            // Don't let the map slide off the screen entirely.
            if (world != null && world.Grid.WorldSize.x > 0f)
            {
                Vector2 size = world.Grid.WorldSize;
                var p = transform.position;
                p.x = Mathf.Clamp(p.x, 0f, size.x);
                p.y = Mathf.Clamp(p.y, 0f, size.y);
                transform.position = p;
            }
        }
    }
}
