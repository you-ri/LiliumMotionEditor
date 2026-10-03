using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;

namespace Lilium
{
    

/**
 * Track GUI
 */
public class TrackGUI
{
	static int sliderHash;
	static Vector2 position_;
	static float value_;
	static float draggingValue_;
	static bool isDragging_ = false;
	static bool isOutside_ = false;
	
	public static Rect GetLastRect()
	{
		return trackRect_;
	}
	
	public static Vector2 GetPosition (float value) 
	{
		float limitLangth = maxLimit_ - minLimit_;
		Vector2 position;
		position.x = trackRect_.x + (value - minLimit_) / limitLangth * trackRect_.width;
		position.y = trackRect_.y;
		return position;
	}
		

	public static float Slider (float width, float value, float minLimit, float maxLimit, string text)
	{
		Rect bgRect = GUILayoutUtility.GetLastRect ();
		return DoSlider (bgRect, value, value, minLimit, maxLimit, width, true, new GUIContent (text), GUI.skin.button);
	}
		
	static float DoSlider (Rect background, float minValue, float maxValue, float minLimit, float maxLimit, float padding, bool isDraggingValue, GUIContent content, GUIStyle style)
	{
		float scale = (maxLimit - minLimit) / background.width;
		maxValue = Mathf.Min (maxLimit, maxValue);
		minValue = Mathf.Max (minLimit, minValue);
		float valueLength = maxValue - minValue;
		float limitLangth = maxLimit - minLimit;

		Rect position = background;
		position.xMin = background.x + (((int)minValue) - minLimit) / limitLangth * background.width - padding;
		position.xMax = background.x + (((int)maxValue) - minLimit) / limitLangth* background.width + padding;
		position.yMin = background.yMin + 0;
		position.yMax = background.yMax - 0;
			
		int controlID = GUIUtility.GetControlID (TrackGUI.sliderHash, FocusType.Passive, position);
		EventType typeForControl = Event.current.GetTypeForControl (controlID);
		
		if (typeForControl == EventType.MouseDown) {
			if (position.Contains (Event.current.mousePosition)) {
				GUIUtility.hotControl = controlID;
				position_ = Event.current.mousePosition;
				value_ = minValue;
				Event.current.Use ();
			}
		}
		else if (typeForControl == EventType.MouseDrag) {
			if (GUIUtility.hotControl == controlID) {
				if (isDraggingValue == true) {
					minValue = value_ + (Event.current.mousePosition.x - position_.x) * scale;
				}
				else {
					draggingValue_ = value_ + (Event.current.mousePosition.x - position_.x) * scale;
					isDragging_ = true;
					isOutside_ = !background.Contains (Event.current.mousePosition);
				}

				Event.current.Use ();
			}
		}
		else if (typeForControl == EventType.MouseUp) {
			if (GUIUtility.hotControl == controlID) {
				minValue = value_ + (Event.current.mousePosition.x - position_.x) * scale;
					
				bool isOutsided = isOutside_;
				GUIUtility.hotControl = 0;
				isDragging_ = false;
				isOutside_ = false;
				Event.current.Use ();
				
				if (isOutsided == true) return float.MaxValue;	
			}
		}
		else if (typeForControl == EventType.Repaint) {
			if (GUIUtility.hotControl == controlID) {
				Rect draggingPosition = position;
				draggingPosition.xMin = background.x + (((int)draggingValue_) - minLimit) / limitLangth * background.width - padding;
				draggingPosition.xMax = background.x + (((int)(draggingValue_+valueLength)) - minLimit) / limitLangth * background.width + padding;
				if (isOutside_ ) {
					Color prevColor = GUI.color;
					GUI.color = Color.red * 0.8f;
					style.Draw (draggingPosition, content, controlID, false);
					GUI.color = prevColor;
				}
				else if (isDragging_ ) {
					style.Draw (draggingPosition, content, controlID, true);
				}
			}
			style.Draw (position, content, controlID, GUIUtility.hotControl == controlID ? true : false);
		}

			
		return Mathf.Clamp(minValue, minLimit, maxLimit);
	}
		
	static Rect trackRect_;
	static Color prevColor_;
	static Color prevBackgroundColor_;
	static float minLimit_;
	static float maxLimit_;
		
	public static void BeginTrack (Rect position, float minLimit, float maxLimit, string text)
	{
		prevColor_ = GUI.color;	
		prevBackgroundColor_ = GUI.color;
			
		TextAnchor backAlinment = GUI.skin.box.alignment;
		RectOffset backMargin = GUI.skin.box.margin;
		GUI.skin.box.margin = new RectOffset(0, 0, 0, 0);
		GUI.skin.box.alignment = TextAnchor.MiddleRight;
		GUI.backgroundColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);
		GUI.Box (position, text);
		GUI.skin.box.alignment = backAlinment;
		GUI.skin.box.margin = backMargin;

		trackRect_ = position;
		minLimit_ = minLimit;
		maxLimit_ = maxLimit;
	}

	public static void BeginTrackLayout (float minLimit, float maxLimit, string text, params GUILayoutOption[] option)
	{
		prevColor_ = GUI.color;	
		prevBackgroundColor_ = GUI.color;
			
		TextAnchor backAlinment = GUI.skin.box.alignment;
		GUI.skin.box.alignment = TextAnchor.MiddleRight;
		//GUI.backgroundColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);
		GUILayout.Box (text, option);
		GUI.skin.box.alignment = backAlinment;
		
		trackRect_ = GUILayoutUtility.GetLastRect ();
		minLimit_ = minLimit;
		maxLimit_ = maxLimit;
	}
		
		
	public static void EndTrack ()
	{
		GUI.color = prevColor_;	
		GUI.backgroundColor = prevBackgroundColor_;
	}
		
	public static float Thumb (int width, float value)
	{
		return DoSlider (trackRect_, value, value, minLimit_, maxLimit_, width/2, false, new GUIContent (""), GUI.skin.box);
	}

	public static float MinMaxSlider (float minValue, float maxValue, string text)
	{
		return DoSlider (trackRect_, minValue, maxValue, minLimit_, maxLimit_, 0, false, new GUIContent (text), GUI.skin.box);
	}
		
	public static float MinMaxSlider (float minValue, float maxValue, int width = 0)
	{
		return DoSlider (trackRect_, minValue, maxValue, minLimit_, maxLimit_, width/2, false, new GUIContent (""), GUI.skin.box);
	}
		
		
	static TrackGUI ()
	{
		sliderHash = "AxisSlider".GetHashCode ();
	}
}

}
