using UnityEngine;

namespace Lilium
{
public struct HitInfo
{
	public Vector3 point;
	public float distance;
	public static HitInfo unfixed = new HitInfo { point = Vector3.zero, distance = -1 };
}
	
public struct Sphere
{
	public Vector3 center;
	public float radius;
}

	
/**
  * Geometry Math
  */
public static class GeometryMath
{
	static Plane plane_ = new Plane ();
		
	private static bool SameSide (Vector3 p1, Vector3 p2, Vector3 a, Vector3 b)
	{
		Vector3 cp1 = Vector3.Cross (b - a, p1 - a);
		Vector3 cp2 = Vector3.Cross (b - a, p2 - a);
		return (Vector3.Dot (cp1, cp2) >= 0) ? true : false;
	}

	public static bool RaycastToPolygon (Ray ray, Vector3 pos1, Vector3 pos2, Vector3 pos3, out HitInfo hitInfo)
	{
		plane_.Set3Points (pos1, pos2, pos3);
		float enter;
		if (plane_.Raycast (ray, out enter)) {
			Vector3 hit = ray.GetPoint (enter);
			if (SameSide (hit, pos1, pos2, pos3) && SameSide (hit, pos2, pos1, pos3) && SameSide (hit, pos3, pos1, pos2)) {
				hitInfo.point = hit;
				hitInfo.distance = enter;
				return true;
			}
		}
			
		hitInfo = HitInfo.unfixed;		
		return false;
	}
		
		
	/**
	  *  線と点との距離を算出
	  */
	public static Vector3 GetClosestPointToLine (Vector3 point, Vector3 beginPoint, Vector3 endPoint)
	{
		Vector3 dv = point - beginPoint;
		Vector3 rv = endPoint - beginPoint;
		float dp = Vector3.Dot (dv, rv);
		float bl = rv.magnitude;
		float pl = dp / bl;
		return rv * (pl / bl) + beginPoint;
	}

	/**
	  *  線（beginPoint → endPoint を伸ばした直線）の上で、光線にいちばん近い点。
	  *  光線の起点を線へ落とすだけだと、透視のカメラでは起点（近いクリップ面の上）がマウスに合わせてほとんど動かず、軸をつかんでも点が動かない。
	  *  光線と線がほぼ平行（軸をまっすぐ見ている）なら決まらないので false
	  */
	public static bool GetClosestPointToRay (Ray ray, Vector3 beginPoint, Vector3 endPoint, out Vector3 point)
	{
		Vector3 u = endPoint - beginPoint;
		Vector3 d = ray.direction;
		Vector3 w = ray.origin - beginPoint;
		float a = Vector3.Dot (d, d);
		float b = Vector3.Dot (d, u);
		float c = Vector3.Dot (u, u);
		float denom = a * c - b * b;
		if (denom <= 1e-6f * a * c) {
			point = beginPoint;
			return false;
		}
		float t = (a * Vector3.Dot (u, w) - b * Vector3.Dot (d, w)) / denom;
		point = beginPoint + u * t;
		return true;
	}

	/**
	  *  線と点との距離を算出
	  */
	/**
	 *  refernece: http://www5.ocn.ne.jp/~tane/prog/resource/3d_doc/3d_memo_apply.html
	 */
	public static bool RaycastToShpere (Ray ray, Sphere sphere, out HitInfo hitInfo)
	{
		Vector3 rd = ray.direction;					
		Vector3 vep = sphere.center - ray.origin;
		Vector3 vex = (Vector3.Dot (vep, rd) / rd.magnitude) * rd.normalized;
		Vector3 vpx = vex - vep;
				
		if (vpx.magnitude <= sphere.radius) {
			Vector3 p1 = hitInfo.point = ray.origin + vex;
			float r0 = sphere.radius;
			float r1 = vpx.magnitude;
			float t = Mathf.Sqrt (r0 * r0 - r1 * r1);
			Vector3 p2 = rd.normalized * t + p1;
			Vector3 p3 = -rd.normalized * t + p1;

			hitInfo.point = ((p2 - ray.origin).sqrMagnitude < (p3 - ray.origin).sqrMagnitude) ? p2 : p3;
			hitInfo.distance = (hitInfo.point - ray.origin).magnitude;
			return true;
		}
			
		hitInfo = HitInfo.unfixed;
		return false;
	}
}
	
}
