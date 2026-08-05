using System;
using System.Reflection;

namespace CivilizameMP.Utils
{
    public static class ReflectionUtils
    {
        public static T GetFieldValue<T>(object obj, string fieldName)
        {
            var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"Campo no encontrado: {fieldName} en {obj.GetType().Name}");
                return default;
            }
            return (T)field.GetValue(obj);
        }

        public static void SetFieldValue(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"Campo no encontrado: {fieldName} en {obj.GetType().Name}");
                return;
            }
            field.SetValue(obj, value);
        }

        public static T GetPropertyValue<T>(object obj, string propertyName)
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"Propiedad no encontrada: {propertyName} en {obj.GetType().Name}");
                return default;
            }
            return (T)prop.GetValue(obj);
        }

        public static void SetPropertyValue(object obj, string propertyName, object value)
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"Propiedad no encontrada: {propertyName} en {obj.GetType().Name}");
                return;
            }
            prop.SetValue(obj, value);
        }

        public static MethodInfo GetMethod(object obj, string methodName, Type[] parameterTypes = null)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            if (parameterTypes != null)
            {
                return obj.GetType().GetMethod(methodName, flags, null, parameterTypes, null);
            }
            return obj.GetType().GetMethod(methodName, flags);
        }

        public static T InvokeMethod<T>(object obj, string methodName, params object[] parameters)
        {
            var method = GetMethod(obj, methodName);
            if (method == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"Método no encontrado: {methodName} en {obj.GetType().Name}");
                return default;
            }
            return (T)method.Invoke(obj, parameters);
        }

        public static void InvokeMethod(object obj, string methodName, params object[] parameters)
        {
            var method = GetMethod(obj, methodName);
            if (method == null)
            {
                CivilizameMPPlugin.Log.LogWarning($"Método no encontrado: {methodName} en {obj.GetType().Name}");
                return;
            }
            method.Invoke(obj, parameters);
        }
    }
}