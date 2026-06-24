using UnityEngine;
using UnityEngine.EventSystems;

namespace VNyanWeightStudio
{
    /// <summary>Drag-to-move the plugin window by its background.</summary>
    public class WindowDrag : MonoBehaviour, IDragHandler, IPointerDownHandler
    {
        public RectTransform dragRect;

        void Awake()
        {
            if (dragRect == null) dragRect = transform as RectTransform;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (dragRect != null) dragRect.anchoredPosition += eventData.delta;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (dragRect != null) dragRect.SetAsLastSibling();
        }
    }
}
