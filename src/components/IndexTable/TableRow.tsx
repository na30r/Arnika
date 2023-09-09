import React from 'react';
import { IndexImportance } from '../../entities/IndexImportance';
import { Company } from '../../entities/Company';
import { SampleColors } from '../../sampleData/sample-colors';

interface TableRowProps {
  indexImportances: IndexImportance[];
  companies: Company[];
  sumIndexImportance: number;
  height: number;
}

const TableRow: React.FC<TableRowProps> = ({
  indexImportances,
  companies,
  sumIndexImportance,
  height,
}) => {
  function heightCalc(index: number) {
    return (index / sumIndexImportance) * height;
  }
  const baseCalculator = (indexImportance: IndexImportance, value?: number): number => {
    if (!value) {
      return 0;
    }
    const range = indexImportance.max - indexImportance.min;
    const base = range / SampleColors.length;
    const section = Math.ceil((value - indexImportance.min) / base);
    return Math.min(Math.max(section, 0), SampleColors.length - 1);
  };

  const colorCalculator = (baseNumber: number): string =>
    SampleColors.find((color) => color.id === baseNumber)?.color ||
    SampleColors[SampleColors.length - 1].color;

  const tdHoverStyle = {
    transition: 'filter 0.3s ease-in-out', // Add a smooth transition effect
  };

  const handleMouseOver = (e: any) => {
    e.currentTarget.style.filter = 'brightness(1.2)'; // Make it lighter
    Object.assign(e.currentTarget.style, tdHoverStyle);
  };

  const handleMouseOut = (e: any) => {
    e.currentTarget.style.filter = ''; // Revert to original brightness
    Object.assign(e.currentTarget.style, {});
  };

  return (
    <tbody>
      {indexImportances.map((ii) => (
        <>
          <tr key={ii.id}>
            <td key={ii.name} height={heightCalc(ii.value)}>
              {' '}
              {ii.name}
            </td>
            {companies.map((company) => (
              <td
                key={company.companyIndex}
                style={{
                  borderRadius: 10,
                  backgroundColor: colorCalculator(
                    baseCalculator(ii, company.shkhesha.find((s) => s.id === ii.id)?.value),
                  ),
                }}
                title={`${ii.name} : ${company.shkhesha.find((s) => s.id === ii.id)?.value || 0}`}
                onMouseOver={handleMouseOver}
                // Remove the hover style on mouse out
                onMouseOut={handleMouseOut}
              ></td>
            ))}
          </tr>
        </>
      ))}
    </tbody>
  );
};
export default TableRow;
