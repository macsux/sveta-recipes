using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Costing;

/// <summary>Volume ↔ weight conversion through a substance's density (legacy "Substance conversion").</summary>
public static class DensityConverter
{
    /// <summary>
    /// Converts <paramref name="qty"/> of <paramref name="from"/> into <paramref name="to"/>. Unit coefficients are
    /// grams per unit when positive and −millilitres per unit when negative.
    /// </summary>
    public static double Convert(double qty, DensityConversionUnit from, DensityConversionUnit to, double densityGPerMl)
    {
        var grams = from.Coefficient > 0 ? qty * from.Coefficient : qty * -from.Coefficient * densityGPerMl;
        return to.Coefficient > 0 ? grams / to.Coefficient : grams / densityGPerMl / -to.Coefficient;
    }

    /// <summary>Price per kg of a pack (legacy CalcPricePerKilo); null for non-weight units.</summary>
    public static decimal? PricePerKg(UnitConverter units, string unit, double packQty, decimal packPrice)
    {
        var kg = units.Get("kg", unit);
        if (kg.IsMissing || packQty <= 0) return null;
        return packPrice * (decimal)(kg.Factor / packQty);
    }
}
