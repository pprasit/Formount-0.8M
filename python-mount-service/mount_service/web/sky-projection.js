// Local horizontal coordinates: x east, y north, z above the horizon.
export function projectSky(azimuth,altitude,yaw,tilt){const a=azimuth*Math.PI/180+ yaw,b=altitude*Math.PI/180,x=Math.cos(b)*Math.sin(a),y=Math.cos(b)*Math.cos(a),z=Math.sin(b);return {x,y:y*Math.cos(tilt)-z*Math.sin(tilt),depth:y*Math.sin(tilt)+z*Math.cos(tilt)};}
