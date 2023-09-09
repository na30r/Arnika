import { useEffect, useState } from 'react';
import { Company } from '../../entities/Company';
import TableHeader from './TableHeader';
import TableRow from './TableRow';
import CompanyApi from '../../services/companyApi';
import { IndexImportance } from '../../entities/IndexImportance';
import indexImportanceApi from '../../services/indexImportanceApi';
import { Col, Row } from 'antd';
import { CompanySelect } from '../form/CompanySelect';

function IndexTable(
  ref: React.MutableRefObject<null>,
  render: () => void,
  width: number,
  height: number,
) {
  const [companies, setCompanies] = useState<Company[]>([]);
  const [selectedCompany, setselectedCompany] = useState<number[] | null>(null);

  const [indexImportance, setIndexImportance] = useState<IndexImportance[]>([]); // اهمیت شاخص ها
  const [sumIndexImportance, setSumIndexImportance] = useState<number>(1);

  useEffect(() => {
    render();
  }, [companies]);

  useEffect(() => {
    async function fetchCompanies() {
      const result = await new CompanyApi().getAllbyIds({}, selectedCompany);
      setCompanies(result.results);
    }
    fetchCompanies();
  }, [selectedCompany]);

  useEffect(() => {
    async function fetchIndexImportance() {
      const result = await new indexImportanceApi().getAll({});
      setIndexImportance(result.results);
      const sum = result.results.reduce((total, index) => total + index.value, 0);
      setSumIndexImportance(sum);
    }
    fetchIndexImportance();
  }, []);

  return (
    <>
      <Row>
        <Col span={24}>
          <CompanySelect onChange={(e: any) => setselectedCompany(e)} />
        </Col>
      </Row>
      <Row>
        <table ref={ref} width={width} className='main-table'>
          <TableHeader companies={companies} />
          <TableRow
            companies={companies}
            indexImportances={indexImportance}
            sumIndexImportance={sumIndexImportance}
            height={height}
          />
        </table>
      </Row>
    </>
  );
}
export default IndexTable;
