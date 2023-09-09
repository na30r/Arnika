import { useEffect, useState } from 'react';
import { Form, Select, SelectProps } from 'antd';
import CompanyApi from '../../services/companyApi';
import { Company } from '../../entities/Company';

export function CompanySelect({ onChange }: SelectProps<string>) {
  const [companies, setCompanies] = useState<Company[]>([]);
  useEffect(() => {
    async function fetchCompanies() {
      const result = await new CompanyApi().getAll({});
      setCompanies(result.results);
    }
    fetchCompanies();
  }, []);
  return (
    <Form.Item label='فیلتر کردن'>
      <Select
        showSearch
        mode='multiple'
        placeholder='شرکت'
        filterOption={false}
        onChange={onChange}
        style={{ width: '100%' }}
      >
        {companies?.map((company: Company) => (
          <Select.Option key={company.id} value={company.id}>
            {company.name}
          </Select.Option>
        ))}
      </Select>
    </Form.Item>
  );
}
