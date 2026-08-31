// Editor/Window/ScanProgressBar.cs

using GameDistrict.MemoryShield.Brand;
using UnityEngine.UIElements;

namespace GameDistrict.MemoryShield.Window
{
    public class ScanProgressBar : VisualElement
    {
        private readonly VisualElement _fill;
        private readonly Label _label;

        public ScanProgressBar()
        {
            style.height = 20;
            style.marginTop = 8;
            style.marginBottom = 4;
            style.backgroundColor = MSBrandTokens.Taupe;
            style.borderTopLeftRadius = 10;
            style.borderTopRightRadius = 10;
            style.borderBottomLeftRadius = 10;
            style.borderBottomRightRadius = 10;
            style.overflow = UnityEngine.UIElements.Overflow.Hidden;

            _fill = new VisualElement();
            _fill.style.position = Position.Absolute;
            _fill.style.left = 0;
            _fill.style.top = 0;
            _fill.style.bottom = 0;
            _fill.style.width = Length.Percent(0);
            _fill.style.backgroundColor = MSBrandTokens.Gold;
            Add(_fill);

            _label = new Label("");
            _label.style.position = Position.Absolute;
            _label.style.left = 8;
            _label.style.top = 2;
            _label.style.color = MSBrandTokens.Navy;
            _label.style.fontSize = 11;
            Add(_label);
        }

        public void Set(float progress01, string label)
        {
            _fill.style.width = Length.Percent(progress01 * 100f);
            _label.text = label;
        }
    }
}
