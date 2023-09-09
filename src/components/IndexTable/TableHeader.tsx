import React, { useEffect, useState } from 'react';
import { Company } from '../../entities/Company';

interface TableHeaderProps {
  companies: Company[];
}

const TableHeader: React.FC<TableHeaderProps> = ({ companies }) => {
  const [sumCompaniesValue, setCompaniesValue] = useState<number>(1); //ارزش کل شرکت ها
  useEffect(() => {
    const sum = companies.reduce((total, company) => total + company.companyIndex, 0);
    setCompaniesValue(sum);
  }, [companies]);

  return (
    <thead>
      <tr>
        <td key={'first-col'} width={'20%'}></td>
        {companies.map((a) => (
          <th key={a.name} style={{ width: `${(a.companyIndex / sumCompaniesValue) * 80}%` }}>
            {a.name}
          </th>
        ))}
      </tr>
    </thead>
  );
};

export default TableHeader;
